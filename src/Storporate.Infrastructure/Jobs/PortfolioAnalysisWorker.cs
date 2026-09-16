using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Storporate.Infrastructure.Jobs;

/// <summary>
/// One unit of work the polling worker can claim and run. Concrete processors
/// (e.g. <c>Storporate.Modules.Portfolio.Analysis.PortfolioAnalysisJobProcessor</c>)
/// implement this interface so the polling worker lives in
/// <see cref="Storporate.Infrastructure"/> and stays independent of any one
/// module's types — matching the established "Infrastructure has no reference
/// back to Modules" rule.
/// </summary>
public interface IBackgroundJobProcessor
{
    /// <summary>
    /// Try to claim one <c>Pending</c> job of the processor's type and process
    /// it to a terminal state. Returns <see cref="BackgroundJobTickOutcome.NoWork"/>
    /// when no <c>Pending</c> job is visible; <see cref="BackgroundJobTickOutcome.ClaimedByAnother"/>
    /// when a concurrent tick beat us; and <see cref="BackgroundJobTickOutcome.Processed"/>
    /// when this call did the work.
    /// </summary>
    Task<BackgroundJobTickOutcome> TryProcessOneAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of one <see cref="IBackgroundJobProcessor.TryProcessOneAsync"/>
/// attempt — see <see cref="IBackgroundJobProcessor.TryProcessOneAsync"/> for the
/// per-value semantics.</summary>
public enum BackgroundJobTickOutcome
{
    /// <summary>A pending job was found and processed to a terminal state. The
    /// worker should re-poll promptly in case more are queued.</summary>
    Processed,

    /// <summary>No pending jobs were visible. The worker should wait the
    /// polling interval before re-checking.</summary>
    NoWork,

    /// <summary>A pending job was found but a concurrent tick (or another
    /// process) already claimed it. The worker should re-poll promptly.</summary>
    ClaimedByAnother,
}

/// <summary>
/// The first <see cref="BackgroundService"/> in this codebase — the STOR-38
/// Phase 2 background worker. Polls the shared <c>Jobs</c> table and hands each
/// <c>Pending</c> row to a registered <see cref="IBackgroundJobProcessor"/> on
/// a fresh service scope per tick, so the per-job <see cref="Storporate.Infrastructure.Persistence.WriteDbContext"/>
/// lives exactly as long as the tick that processed it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a polling loop instead of a push-based trigger.</b> The plan
/// explicitly rules out SignalR / push for STOR-38 (out of scope — see the
/// STOR-38 plan, Out of Scope section). A short-interval poll is the cheapest
/// delivery mechanism that satisfies the user-visible expectation
/// "after I submit, analysis begins within a few seconds" without introducing
/// a new transport or a queueing system. The 5-second interval is a starting
/// value; a future story can tune it once there's real load data.
/// </para>
/// <para>
/// <b>Scope per tick.</b> The processor handles exactly one job per tick —
/// that's the smallest unit that keeps the worker simple and matches the
/// "claim a single Pending row" semantics of
/// <see cref="IBackgroundJobProcessor.TryProcessOneAsync"/>. A future story
/// that wants higher throughput can widen the tick into "process up to N jobs"
/// without changing the row-claim contract.
/// </para>
/// <para>
/// <b>Exception isolation.</b> A bug in the processor (or an unexpected
/// LlmProviderException that escapes the retry envelope) would otherwise
/// crash the hosted service and stop all subsequent processing. We catch and
/// log at this layer so a single misbehaving tick can't take the whole worker
/// down; the next tick still runs.
/// </para>
/// <para>
/// <b>Stopping.</b> Standard <see cref="BackgroundService"/> contract:
/// <see cref="ExecuteAsync"/> exits cleanly when the host stops (e.g. on
/// SIGINT). Any job mid-tick at that moment runs to completion because the
/// scope is awaited in full; an in-flight LLM call is cancelled by its own
/// token, leaving the job at <c>Running</c> for a future worker (or operator)
/// to clean up.
/// </para>
/// </remarks>
public sealed class PortfolioAnalysisWorker : BackgroundService
{
    /// <summary>How often the worker re-checks for <c>Pending</c> jobs when the
    /// previous tick saw no work. 5 seconds matches the plan's "polling on a
    /// short interval (e.g. every 5 seconds)" baseline.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PortfolioAnalysisWorker> _logger;

    public PortfolioAnalysisWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PortfolioAnalysisWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PortfolioAnalysisWorker started; polling every {PollInterval}.", PollInterval);

        // Standard "loop until the host stops" pattern. We deliberately do not
        // Task.Delay(...).ContinueWith a cancellation handler — when the
        // stoppingToken fires, the awaiting Task.Delay returns a canceled task
        // and the catch+rethrow block lets the host shut down cleanly.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();

                // Every processor the worker drives must register itself as
                // IBackgroundJobProcessor; if a story forgets that step the
                // container throws here at startup-tick time, which is the
                // right place for that mistake to surface.
                var processors = scope.ServiceProvider
                    .GetServices<IBackgroundJobProcessor>()
                    .ToList();

                BackgroundJobTickOutcome? lastOutcome = null;
                foreach (var processor in processors)
                {
                    var outcome = await processor
                        .TryProcessOneAsync(stoppingToken)
                        .ConfigureAwait(false);

                    lastOutcome = outcome;
                    if (outcome == BackgroundJobTickOutcome.Processed
                        || outcome == BackgroundJobTickOutcome.ClaimedByAnother)
                    {
                        // Re-poll promptly; another tick worth of work may
                        // already be queued behind the one we just processed.
                        break;
                    }
                }

                if (lastOutcome is null || lastOutcome == BackgroundJobTickOutcome.NoWork)
                {
                    // No work this tick — wait the polling interval before
                    // re-checking.
                    try
                    {
                        await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a single misbehaving tick take the worker down.
                // Log, wait, and try again. The job itself is left at Running
                // here — a future story can add a "stale-Running reaper" if
                // this proves to be a real problem in practice.
                _logger.LogError(ex, "Unhandled error in PortfolioAnalysisWorker loop; will retry on the next interval.");
                try
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("PortfolioAnalysisWorker stopped.");
    }
}
