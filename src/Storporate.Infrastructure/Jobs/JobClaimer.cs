using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Jobs;

/// <summary>
/// Shared claim + bookkeeping primitives for the STOR-40 background-job
/// processors. Phase 2 introduces this helper so the new
/// <c>AdvisorTurnJobProcessor</c> and <c>CompareExplorationsJobProcessor</c>
/// don't each rebuild the same Postgres atomic-claim / InMemory load-then-flip
/// machinery the original <see cref="Storporate.Modules.Portfolio.Analysis.PortfolioAnalysisJobProcessor"/>
/// grew organically. The portfolio processor is intentionally NOT refactored in
/// this phase — its inline claim stays untouched and continues to work against
/// the same <c>Jobs</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two claim branches, one helper.</b>
/// <list type="bullet">
/// <item>Production Postgres uses <see cref="RelationalQueryableExtensions.ExecuteUpdateAsync"/> with
/// <c>WHERE Id = ? AND Status = 'Pending' AND Type = ?</c> so the claim is
/// atomic at the row level — only one tick (or one process) ever flips the
/// row to <see cref="JobStatus.Running"/>; the rest see zero rows affected
/// and get <see cref="BackgroundJobTickOutcome.ClaimedByAnother"/>.</item>
/// <item>Unit tests run against the EF InMemory provider which does not
/// implement <c>ExecuteUpdateAsync</c>. The InMemory branch loads the
/// candidate row under a process-wide lock, re-checks the status, flips
/// it, and commits — mirroring the Postgres branch's "load, re-check,
/// flip" semantics without the atomic UPDATE.</item>
/// </list>
/// </para>
/// <para>
/// <b>Caller scope bracket.</b> Both branches assume the caller has already
/// opened <c>IBackgroundAccountScope.BeginSystemScope()</c> — the EF global
/// query filter's <c>IsAdministrator</c> short-circuit must be true so the
/// load + flip see every <c>Pending</c> row regardless of account. The
/// helper does not open or close the scope itself; that's the caller's job
/// because the same scope also covers the post-claim per-account work
/// (which is open with a different <c>BeginAccountScope</c> bracket).
/// </para>
/// <para>
/// <b>Why a static helper and not a service.</b> Both methods are
/// side-effecting only against the supplied <see cref="WriteDbContext"/> and
/// <see cref="ILogger"/> — no per-call state to inject. A static helper
/// keeps the call site tight and avoids dragging a new DI lifetime into the
/// processor's constructor chain.
/// </para>
/// </remarks>
public static class JobClaimer
{
    /// <summary>
    /// The result of one <see cref="ClaimOneAsync"/> call. <see cref="Outcome"/>
    /// is the worker's tick-level signal (<see cref="BackgroundJobTickOutcome.Processed"/>
    /// / <see cref="BackgroundJobTickOutcome.NoWork"/> / <see cref="BackgroundJobTickOutcome.ClaimedByAnother"/>);
    /// <see cref="Job"/> is non-null only when <see cref="Outcome"/> is
    /// <see cref="BackgroundJobTickOutcome.Processed"/> and carries the
    /// just-claimed row (detached, ready for the caller to re-attach or
    /// copy fields off).
    /// </summary>
    public sealed record ClaimResult(BackgroundJobTickOutcome Outcome, Job? Job);

    /// <summary>
    /// Process-wide lock that serializes the InMemory branch's
    /// "load candidate -> recheck -> flip to Running" sequence. Postgres
    /// uses its own atomic UPDATE WHERE and does not take this lock.
    /// Mirrors the private <c>InMemoryClaimLock</c> on the portfolio
    /// processor; the two locks are independent (each manages its own
    /// test seam) because the portfolio processor is not refactored in
    /// this phase.
    /// </summary>
    internal static readonly object InMemoryClaimLock = new();

    /// <summary>
    /// Claim one <c>Pending</c> <see cref="Job"/> of the supplied
    /// <paramref name="jobType"/>, flipping it to <see cref="JobStatus.Running"/>
    /// with <see cref="Job.StartedAt"/> set to <paramref name="nowUtc"/>.
    /// Returns the matching <see cref="ClaimResult"/>. The caller must
    /// have opened <see cref="Storporate.Infrastructure.Authorization.IBackgroundAccountScope.BeginSystemScope"/>
    /// before calling so the EF global query filter's
    /// <c>IsAdministrator</c> short-circuit makes every <c>Pending</c> row
    /// visible.
    /// </summary>
    public static async Task<ClaimResult> ClaimOneAsync(
        WriteDbContext dbContext,
        string jobType,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);

        var isInMemoryProvider = dbContext.Database.ProviderName?.Contains(
            "InMemory", StringComparison.OrdinalIgnoreCase) == true;

        return isInMemoryProvider
            ? ClaimOneInMemoryAsync(dbContext, jobType, nowUtc)
            : await ClaimOnePostgresAsync(dbContext, jobType, nowUtc, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<ClaimResult> ClaimOnePostgresAsync(
        WriteDbContext dbContext,
        string jobType,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var pendingJobId = await dbContext.Jobs
            .Where(job => job.Type == jobType && job.Status == JobStatus.Pending)
            .OrderBy(job => job.CreatedAt)
            .Select(job => job.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (pendingJobId == Guid.Empty)
        {
            return new ClaimResult(BackgroundJobTickOutcome.NoWork, null);
        }

        var claimedRows = await dbContext.Jobs
            .Where(job => job.Id == pendingJobId
                && job.Status == JobStatus.Pending
                && job.Type == jobType)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(job => job.Status, JobStatus.Running)
                    .SetProperty(job => job.StartedAt, nowUtc)
                    .SetProperty(job => job.UpdatedAt, nowUtc),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimedRows == 0)
        {
            return new ClaimResult(BackgroundJobTickOutcome.ClaimedByAnother, null);
        }

        var job = await dbContext.Jobs
            .AsNoTracking()
            .FirstAsync(j => j.Id == pendingJobId, cancellationToken)
            .ConfigureAwait(false);

        return new ClaimResult(BackgroundJobTickOutcome.Processed, job);
    }

    private static ClaimResult ClaimOneInMemoryAsync(
        WriteDbContext dbContext,
        string jobType,
        DateTime nowUtc)
    {
        Job? job;
        BackgroundJobTickOutcome outcome;

        lock (InMemoryClaimLock)
        {
            dbContext.ChangeTracker.Clear();

            // Load the next-claim candidate by Type + Status without the
            // Pending filter so that two contexts racing on the same
            // InMemoryDatabaseRoot can observe each other's already-claimed
            // (Running) rows on the recheck below. Filtering on
            // Pending here would silently collapse the race into "no work"
            // — the wrong signal for callers that want to know whether the
            // queue is empty vs. whether someone else already grabbed the
            // head-of-line job.
            var candidate = dbContext.Jobs
                .Where(j => j.Type == jobType
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
                .OrderBy(j => j.CreatedAt)
                .FirstOrDefault();

            if (candidate is null)
            {
                return new ClaimResult(BackgroundJobTickOutcome.NoWork, null);
            }

            // Re-read the row's current Status from the InMemory store
            // (no change-tracker state, no in-flight write) to decide
            // whether the candidate is still up for grabs or has already
            // been claimed by another worker that committed between our
            // load and our recheck.
            var recheck = dbContext.Jobs
                .AsNoTracking()
                .Where(j => j.Id == candidate.Id)
                .Select(j => new { j.Status })
                .FirstOrDefault();

            if (recheck is null)
            {
                // Row vanished between load and recheck — treat the same
                // as a competing claim to keep the caller's retry logic
                // uniform.
                return new ClaimResult(BackgroundJobTickOutcome.ClaimedByAnother, null);
            }

            if (recheck.Status != JobStatus.Pending)
            {
                // Some other worker already flipped this row out of
                // Pending (most likely to Running). Return the race-loss
                // signal so the caller can distinguish a busy queue from
                // an empty one.
                return new ClaimResult(BackgroundJobTickOutcome.ClaimedByAnother, null);
            }

            candidate.Status = JobStatus.Running;
            candidate.StartedAt = nowUtc;
            candidate.UpdatedAt = nowUtc;
            dbContext.SaveChanges();

            job = dbContext.Jobs
                .AsNoTracking()
                .First(j => j.Id == candidate.Id);
            outcome = BackgroundJobTickOutcome.Processed;
        }

        dbContext.ChangeTracker.Clear();
        return new ClaimResult(outcome, job);
    }
}

/// <summary>
/// Terminal-state transitions for jobs the helper-based processors own.
/// Mirrors the inline helpers the portfolio processor grew — extracted
/// here so <see cref="Storporate.Modules.StudentGrowthExperience.Advisor.AdvisorTurnJobProcessor"/>
/// and <see cref="Storporate.Modules.StudentGrowthExperience.Advisor.CompareExplorationsJobProcessor"/>
/// share one retry/fail policy instead of drifting. The portfolio
/// processor still uses its own inline equivalents and is not
/// refactored in this phase.
/// </summary>
public static class JobBookkeeper
{
    /// <summary>Number of attempts after which a job moves to
    /// <see cref="JobStatus.Failed"/>. Attempts 1 and 2 land on
    /// <see cref="JobStatus.Pending"/> via <see cref="RequeueOrFailAsync"/>;
    /// attempt 3 lands on <see cref="JobStatus.Failed"/>.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Mark the supplied <paramref name="job"/> as
    /// <see cref="JobStatus.Succeeded"/>. The job is attached so the change
    /// tracker writes back the transition (Running → Succeeded) without a
    /// full re-SELECT.
    /// </summary>
    public static Task MarkSucceededAsync(
        WriteDbContext dbContext,
        Job job,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(job);

        dbContext.Jobs.Attach(job);
        job.Status = JobStatus.Succeeded;
        job.CompletedAt = nowUtc;
        job.UpdatedAt = nowUtc;
        job.ErrorMessage = null;
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Mark the supplied <paramref name="job"/> as
    /// <see cref="JobStatus.Failed"/> with the supplied error message. Used
    /// for non-retryable failures (malformed payload, missing parent row)
    /// that should not consume a retry attempt.
    /// </summary>
    public static Task MarkFailedAsync(
        WriteDbContext dbContext,
        Job job,
        DateTime nowUtc,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(errorMessage);

        dbContext.Jobs.Attach(job);
        job.Status = JobStatus.Failed;
        job.ErrorMessage = errorMessage;
        job.CompletedAt = nowUtc;
        job.UpdatedAt = nowUtc;
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Outcome of <see cref="RequeueOrFailAsync"/>: tells the caller whether
    /// the job landed back on the queue for another attempt or was moved
    /// to <see cref="JobStatus.Failed"/> for good. The processor uses the
    /// latter signal to flip its parent entity (the
    /// <see cref="Storporate.SharedKernel.Entities.Exploration"/> /
    /// <see cref="Storporate.SharedKernel.Entities.ExplorationComparison"/>)
    /// into its terminal state in the same write batch.
    /// </summary>
    public enum RequeueOrFailOutcome
    {
        /// <summary>Attempt count &lt; <see cref="MaxAttempts"/>: job is back on Pending.</summary>
        Requeued,
        /// <summary>Attempt count ≥ <see cref="MaxAttempts"/>: job is now Failed.</summary>
        Failed,
    }

    /// <summary>
    /// Increment <see cref="Job.AttemptCount"/> and either re-queue the
    /// job as <see cref="JobStatus.Pending"/> (when the new attempt count
    /// is below <see cref="MaxAttempts"/>) or mark it
    /// <see cref="JobStatus.Failed"/> with the supplied error message
    /// (when the cap has been hit). The status flip happens in the same
    /// <see cref="DbContext.SaveChangesAsync(bool, CancellationToken)"/>
    /// call as the attempt-count bump so a half-committed state is
    /// impossible.
    /// </summary>
    /// <returns>The terminal outcome so the caller can mirror the
    /// transition onto its parent entity.</returns>
    public static async Task<RequeueOrFailOutcome> RequeueOrFailAsync(
        WriteDbContext dbContext,
        Job job,
        DateTime nowUtc,
        string errorMessage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(errorMessage);
        ArgumentNullException.ThrowIfNull(logger);

        var nextAttempt = job.AttemptCount + 1;
        dbContext.Jobs.Attach(job);
        job.AttemptCount = nextAttempt;
        job.ErrorMessage = errorMessage;
        job.UpdatedAt = nowUtc;

        if (nextAttempt < MaxAttempts)
        {
            // Re-queue for another tick. Status back to Pending (not Running) so
            // the row-claim UPDATE matches it again on the next pass.
            job.Status = JobStatus.Pending;
            job.StartedAt = null;
            job.CompletedAt = null;
            logger.LogWarning(
                "Job {JobId} attempt {Attempt}/{Max} failed ({Error}); re-queuing.",
                job.Id, nextAttempt, MaxAttempts, errorMessage);
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.CompletedAt = nowUtc;
            logger.LogError(
                "Job {JobId} failed after {Max} attempts: {Error}",
                job.Id, MaxAttempts, errorMessage);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return nextAttempt < MaxAttempts
            ? RequeueOrFailOutcome.Requeued
            : RequeueOrFailOutcome.Failed;
    }
}
