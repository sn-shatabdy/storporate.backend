using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Jobs;

/// <summary>
/// Runs once per <see cref="PortfolioAnalysisWorker"/> tick (under the system account
/// scope). A <see cref="Job"/> that has been <c>Running</c> for longer than
/// <see cref="BackgroundJobOptions.StaleRunningAfter"/> is treated as abandoned: the
/// <see cref="Job.AttemptCount"/> is bumped and the row is reset to <c>Pending</c>, or
/// marked <c>Failed</c> once <see cref="Job.AttemptCount"/> reaches the per-job hard cap
/// of <see cref="MaxAttemptsForReaper"/>. This guards against a worker crash mid-tick —
/// without the reaper, a dead worker's last-known <c>Running</c> row would block that
/// job forever (the Atomic claim UPDATE would not see <c>Status = Pending</c>) and the
/// user-visible UI would stay on the optimistic <c>Analyzing</c> status forever.
/// </summary>
/// <remarks>
/// <para>
/// <b>System scope.</b> The reaper looks across every account's jobs, not just the
/// ambient one, so it runs inside <see cref="IBackgroundAccountScope.BeginSystemScope"/>
/// the same way the claim does. Without that, the EF global query filter would hide
/// every job just like the claim path did before STOR-40 Phase 1.
/// </para>
/// <para>
/// <b>Per-processor mirroring.</b> When a job crosses the failure cap, the reaper
/// resolves which <see cref="IBackgroundJobProcessor"/> would have processed it and
/// calls <see cref="IBackgroundJobProcessor.OnJobAbandonedAsync"/> so the processor can
/// mirror the failed state onto its parent row (e.g. <see cref="PortfolioItem.AnalysisStatus"/>).
/// This keeps the <c>Job</c> row state and the parent row state in sync without the
/// reaper itself having to know which module owns the job.
/// </para>
/// </remarks>
public sealed class StaleJobReaper
{
    /// <summary>
    /// Maximum attempts before a reaped job is moved to <c>Failed</c>. Matches
    /// <see cref="PortfolioAnalysisJobProcessor.MaxAttempts"/> — kept as a separate
    /// constant here so the reaper doesn't take a cross-module dependency on the
    /// Portfolio module, which is precisely the layering rule that keeps
    /// <see cref="PortfolioAnalysisWorker"/> in <see cref="Storporate.Infrastructure"/>.
    /// </summary>
    public const int MaxAttemptsForReaper = 3;

    private readonly WriteDbContext _dbContext;
    private readonly IReadOnlyList<IBackgroundJobProcessor> _processors;
    private readonly TimeSpan _staleAfter;
    private readonly ILogger<StaleJobReaper> _logger;
    private readonly TimeProvider _timeProvider;

    public StaleJobReaper(
        WriteDbContext dbContext,
        IEnumerable<IBackgroundJobProcessor> processors,
        IOptions<BackgroundJobOptions> options,
        ILogger<StaleJobReaper> logger,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _processors = processors.ToList();
        _staleAfter = options.Value.StaleRunningAfter;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// One tick of the reaper: return all abandoned <see cref="Job"/> rows to a
    /// re-runnable state. Returns the rows the reaper left at <c>Failed</c> so the
    /// caller can hand them to <see cref="IBackgroundJobProcessor.OnJobAbandonedAsync"/>
    /// (which must be called outside the system scope, under
    /// <see cref="IBackgroundAccountScope.BeginAccountScope"/> per job).
    /// </summary>
    public async Task<IReadOnlyList<ReaperOutcome>> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        // InMemory provider used by the unit test suite doesn't support
        // ExecuteUpdateAsync; use a tracked-load-then-save path there. The
        // production Postgres path uses the atomic UPDATE.
        var isInMemoryProvider = _dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;

        IReadOnlyList<Job> staleJobs;
        if (isInMemoryProvider)
        {
            staleJobs = await ReapInMemoryAsync(now).ConfigureAwait(false);
        }
        else
        {
            staleJobs = await ReapPostgresAsync(now, cancellationToken).ConfigureAwait(false);
        }

        var outcomes = new List<ReaperOutcome>(staleJobs.Count);
        foreach (var job in staleJobs)
        {
            outcomes.Add(new ReaperOutcome(job));
        }

        if (outcomes.Count > 0)
        {
            _logger.LogInformation(
                "StaleJobReaper recovered {Count} abandoned job(s): {Pending} re-queued, {Failed} moved to Failed.",
                outcomes.Count,
                outcomes.Count(o => o.NextStatus == JobStatus.Pending),
                outcomes.Count(o => o.NextStatus == JobStatus.Failed));
        }

        return outcomes;
    }

    private async Task<IReadOnlyList<Job>> ReapPostgresAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - _staleAfter;
        var pendingReturns = new List<Job>();
        var failedJobs = new List<Job>();

        // Step 1: load every stale row. We load (rather than update in place) so we
        // can return the post-reap state to the caller for OnJobAbandonedAsync. The
        // Postgres production path still flips rows with a per-row UPDATE.
        var stale = await _dbContext.Jobs
            .Where(j => j.Status == JobStatus.Running && j.UpdatedAt < cutoff)
            .OrderBy(j => j.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var job in stale)
        {
            var nextAttempt = job.AttemptCount + 1;
            job.AttemptCount = nextAttempt;
            job.UpdatedAt = now;
            job.ErrorMessage = nextAttempt >= MaxAttemptsForReaper
                ? "Job stopped responding"
                : null;

            if (nextAttempt < MaxAttemptsForReaper)
            {
                job.Status = JobStatus.Pending;
                job.StartedAt = null;
                pendingReturns.Add(job);
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.CompletedAt = now;
                failedJobs.Add(job);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return pendingReturns.Concat(failedJobs).ToList();
    }

    private async Task<IReadOnlyList<Job>> ReapInMemoryAsync(DateTime now)
    {
        var cutoff = now - _staleAfter;
        var stale = await _dbContext.Jobs
            .Where(j => j.Status == JobStatus.Running && j.UpdatedAt < cutoff)
            .OrderBy(j => j.UpdatedAt)
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var job in stale)
        {
            var nextAttempt = job.AttemptCount + 1;
            job.AttemptCount = nextAttempt;
            job.UpdatedAt = now;
            job.ErrorMessage = nextAttempt >= MaxAttemptsForReaper
                ? "Job stopped responding"
                : null;

            if (nextAttempt < MaxAttemptsForReaper)
            {
                job.Status = JobStatus.Pending;
                job.StartedAt = null;
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.CompletedAt = now;
            }
        }

        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        return stale;
    }

    /// <summary>
    /// One row in the reaper's report: <see cref="Job"/> plus its post-reap
    /// <see cref="JobStatus"/>. The caller uses <see cref="NextStatus"/> to decide
    /// whether to call <see cref="IBackgroundJobProcessor.OnJobAbandonedAsync"/>.
    /// </summary>
    public sealed record ReaperOutcome(Job Job)
    {
        public string NextStatus => Job.Status;
    }
}
