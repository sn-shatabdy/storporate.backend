using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio.Analysis;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Re-queues an analysis job for a <see cref="PortfolioItem"/> that previously reached
/// <see cref="PortfolioAnalysisStatuses.Failed"/>. The handler is the producer-side twin
/// of <see cref="CreatePortfolioItemHandler"/>'s enqueue path (one
/// <see cref="PortfolioJobTypes.AnalyzePortfolioItem"/> job, <see cref="JobStatus.Pending"/>,
/// payload carrying the item's id) — only the trigger is a user-initiated retry instead
/// of a portfolio submission.
/// </summary>
/// <remarks>
/// <para>
/// <b>State machine gate.</b> Only valid when the item's current
/// <see cref="PortfolioItem.AnalysisStatus"/> is <see cref="PortfolioAnalysisStatuses.Failed"/>.
/// Any other status throws <see cref="PortfolioAnalysisNotRetryableException"/> (mapped
/// to a 409 by <c>GlobalExceptionHandler</c>) because retrying would either:
/// <list type="bullet">
///   <item>double-enqueue against an in-flight <see cref="PortfolioAnalysisStatuses.Analyzing"/>
///   job (the worker is already processing one);</item>
///   <item>silently overwrite a successful <see cref="PortfolioAnalysisStatuses.Analyzed"/>
///   snapshot;</item>
///   <item>be a no-op against <see cref="PortfolioAnalysisStatuses.NotAnalyzed"/>
///   (the existing job is still pending — the worker will pick it up);</item>
///   <item>be permanently hopeless against <see cref="PortfolioAnalysisStatuses.Unsupported"/>
///   (the evidence type is unrecoverable).</item>
/// </list>
/// </para>
/// <para>
/// <b>Status reset.</b> Flips the item back to <see cref="PortfolioAnalysisStatuses.NotAnalyzed"/>
/// and clears <see cref="PortfolioItem.LastAnalyzedAt"/> so the badge on the portfolio
/// list / detail page reflects "queued, not yet run" immediately — the worker will flip
/// it to <see cref="PortfolioAnalysisStatuses.Analyzing"/> on its next claim, exactly
/// as it does on the original submission path.
/// </para>
/// <para>
/// <b>Why one <c>SaveChangesAsync</c>.</b> The status flip and the new
/// <see cref="Job"/> row commit atomically: a half-committed state (item reset but
/// no job, or job queued but item still showing <c>Failed</c>) is impossible, just like
/// the create path's "item + initial job" commit.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> <see cref="PortfolioItem"/> is <see cref="IAccountScoped"/>,
/// so the EF Core global query filter restricts the read to the caller's account. A
/// cross-account id never matches and the handler returns an outcome with
/// <see cref="RetryOutcome.Enqueued"/> equal to <see langword="false"/> so the endpoint
/// maps to a 404 — same precedent as <see cref="DeletePortfolioItemHandler"/>.
/// </para>
/// </remarks>
public static class RetryPortfolioItemAnalysisHandler
{
    /// <summary>The result of <see cref="ExecuteAsync"/>. A discriminated pair
    /// (<c>Enqueued = false</c> / <c>Enqueued = true</c>) keeps the endpoint's
    /// status-code decision trivial: false -&gt; 404, true -&gt; 202.</summary>
    public sealed record RetryOutcome(bool Enqueued, Guid PortfolioItemId, Guid NewJobId);

    public static async Task<RetryOutcome> ExecuteAsync(
        Guid portfolioItemId,
        WriteDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Tenant isolation: the global query filter restricts the lookup to the
        // caller's account. A cross-account id never matches and the handler
        // returns Enqueued=false so the endpoint maps to a 404.
        var item = await dbContext.PortfolioItems
            .FirstOrDefaultAsync(item => item.Id == portfolioItemId, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            return new RetryOutcome(Enqueued: false, PortfolioItemId: portfolioItemId, NewJobId: Guid.Empty);
        }

        // State machine gate. Every status other than Failed either is in flight,
        // is the happy terminal state, or is permanently unrecoverable — see the
        // class remarks for the per-status rationale.
        if (item.AnalysisStatus != PortfolioAnalysisStatuses.Failed)
        {
            throw new PortfolioAnalysisNotRetryableException(item.AnalysisStatus);
        }

        var now = timeProvider.GetUtcNow();
        var nowUtc = now.UtcDateTime;

        // Reset to the pre-claim initial state so the UI's status badge reflects
        // "queued, not yet run" between the handler returning and the worker's
        // next claim flipping it to Analyzing.
        item.AnalysisStatus = PortfolioAnalysisStatuses.NotAnalyzed;
        item.LastAnalyzedAt = null;

        var newJob = new Job
        {
            Id = Guid.NewGuid(),
            Type = PortfolioJobTypes.AnalyzePortfolioItem,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            PayloadJson = JsonSerializer.Serialize(
                new AnalyzePortfolioItemPayload(item.Id)),
            AccountId = item.AccountId,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
        dbContext.Jobs.Add(newJob);

        // One SaveChangesAsync commits both the status flip and the new job row
        // atomically — a half-committed state (item reset but no job, or job
        // queued but item still showing Failed) is impossible.
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new RetryOutcome(Enqueued: true, PortfolioItemId: item.Id, NewJobId: newJob.Id);
    }
}