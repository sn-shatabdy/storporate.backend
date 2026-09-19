using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Jobs;

/// <summary>
/// STOR-40 Phase 2: pin the shared <see cref="JobClaimer"/> +
/// <see cref="JobBookkeeper"/> helper that the new <c>AdvisorTurnJobProcessor</c>
/// and <c>CompareExplorationsJobProcessor</c> use for claim + retry/fail
/// transitions. The portfolio processor continues to use its own inline
/// equivalents; this suite only covers the shared helper.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the InMemory branch only.</b> Production Postgres claims run
/// through <c>ExecuteUpdateAsync</c> against a real database; the unit-test
/// path uses the EF InMemory provider which doesn't implement
/// <c>ExecuteUpdateAsync</c>. The helper internally branches on
/// <c>Database.ProviderName?.Contains("InMemory")</c> and runs the
/// load-then-flip path against InMemory. That's the only path exercised
/// here — the Postgres branch is covered indirectly by the existing
/// <c>PortfolioAnalysisJobProcessor</c> integration tests and is the
/// verbatim same SQL pattern.
/// </para>
/// </remarks>
public class JobClaimerTests
{
    private const string AdvisorTurnJobType = "AdvisorTurn";
    private const string SomeOtherJobType = "SomeOtherJobType";

    [Fact]
    public async Task ClaimOneAsync_WithPendingJob_ClaimsAndReturnsProcessed()
    {
        // Seed one Pending AdvisorTurn job under the system scope so the EF
        // filter's IsAdministrator short-circuit lets the load see it. After
        // the claim the row must be Running with StartedAt set and the returned
        // ClaimResult must carry (Processed, job) with the same id.
        var (dbContext, jobId) = await SeedSinglePendingJobAsync(AdvisorTurnJobType);

        var result = await JobClaimer.ClaimOneAsync(
            dbContext,
            AdvisorTurnJobType,
            nowUtc: DateTime.UtcNow,
            cancellationToken: CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, result.Outcome);
        Assert.NotNull(result.Job);
        Assert.Equal(jobId, result.Job!.Id);

        var reloaded = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Running, reloaded.Status);
        Assert.NotNull(reloaded.StartedAt);
    }

    [Fact]
    public async Task ClaimOneAsync_WithNoPendingJob_ReturnsNoWork()
    {
        // Seed zero rows — the load returns null and the helper short-circuits
        // to (NoWork, null) before the recheck / flip.
        var dbContext = CreateSystemScopedDbContext("empty-db");

        var result = await JobClaimer.ClaimOneAsync(
            dbContext,
            AdvisorTurnJobType,
            nowUtc: DateTime.UtcNow,
            cancellationToken: CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.NoWork, result.Outcome);
        Assert.Null(result.Job);
    }

    [Fact]
    public async Task ClaimOneAsync_WithPendingOfOtherType_ReturnsNoWork()
    {
        // The Type discriminator filter must exclude other job types. Seed a
        // Pending row of a different type and verify the AdvisorTurn claim
        // misses it.
        var (dbContext, _) = await SeedSinglePendingJobAsync(SomeOtherJobType);

        var result = await JobClaimer.ClaimOneAsync(
            dbContext,
            AdvisorTurnJobType,
            nowUtc: DateTime.UtcNow,
            cancellationToken: CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.NoWork, result.Outcome);
        Assert.Null(result.Job);

        // The seeded row must remain untouched (no cross-type contamination).
        var reloaded = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Pending, reloaded.Status);
    }

    [Fact]
    public async Task ClaimOneAsync_TwoConcurrentClaimsInMemory_OneWinsOneGetsClaimedByAnother()
    {
        // The InMemory branch relies on the static InMemoryClaimLock to
        // serialize the load+recheck+flip window. With two callers backed by
        // separate DbContext instances pointing at the same InMemoryDatabaseRoot
        // the second caller's load-then-recheck step must observe Status=Running
        // (the first caller's claim row) and return ClaimedByAnother rather
        // than flipping the row a second time. Using two contexts (rather than
        // a single shared one) is the only way to faithfully model two workers
        // racing on the same backing store.
        var (seedContext, jobId, databaseName, sharedRoot) =
            await SeedSinglePendingJobSharedRootAsync(AdvisorTurnJobType);

        var ctxA = CreateSystemScopedDbContext(databaseName, sharedRoot);
        var ctxB = CreateSystemScopedDbContext(databaseName, sharedRoot);

        var first = await JobClaimer.ClaimOneAsync(
            ctxA,
            AdvisorTurnJobType,
            nowUtc: DateTime.UtcNow,
            cancellationToken: CancellationToken.None);

        var second = await JobClaimer.ClaimOneAsync(
            ctxB,
            AdvisorTurnJobType,
            nowUtc: DateTime.UtcNow,
            cancellationToken: CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, first.Outcome);
        Assert.Equal(BackgroundJobTickOutcome.ClaimedByAnother, second.Outcome);
        Assert.Null(second.Job);

        // The row must still be Running with AttemptCount = 0 — neither
        // claimant's second pass bumped attempts or otherwise mutated state.
        var verifyContext = CreateSystemScopedDbContext(databaseName, sharedRoot);
        var reloaded = await verifyContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Running, reloaded.Status);
        Assert.Equal(0, reloaded.AttemptCount);
        Assert.Equal(jobId, reloaded.Id);
    }

    [Fact]
    public async Task JobBookkeeper_MarkSucceededAsync_FlipsStatusToSucceededWithCompletedAt()
    {
        // Terminal success transition: Status=Succeeded, CompletedAt=now,
        // UpdatedAt=now, ErrorMessage cleared. The job is re-attached so the
        // change tracker writes the transition without a full SELECT.
        var (dbContext, jobId, databaseName, sharedRoot) =
            await SeedSinglePendingJobSharedRootAsync(AdvisorTurnJobType);
        var job = await dbContext.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);

        // Mark it Running first so the transition (Running -> Succeeded) is
        // realistic — the helper does not require a particular starting
        // state but the field semantics match the Running branch. Use a
        // context that shares the seed's InMemoryDatabaseRoot, otherwise the
        // running-set write lands in a different store from the seed row.
        await using (var runningContext = CreateSystemScopedDbContext(databaseName, sharedRoot))
        {
            var tracked = await runningContext.Jobs.SingleAsync(j => j.Id == jobId);
            tracked.Status = JobStatus.Running;
            tracked.StartedAt = DateTime.UtcNow;
            await runningContext.SaveChangesAsync();
        }

        var now = DateTime.UtcNow;
        await JobBookkeeper.MarkSucceededAsync(dbContext, job, now, CancellationToken.None);

        var reloaded = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, reloaded.Status);
        Assert.Equal(now, reloaded.CompletedAt);
        Assert.Null(reloaded.ErrorMessage);
    }

    [Fact]
    public async Task JobBookkeeper_MarkFailedAsync_FlipsStatusToFailedWithErrorMessage()
    {
        // Non-retryable failure: Status=Failed, CompletedAt=now, ErrorMessage
        // populated, AttemptCount unchanged. Used for malformed-payload and
        // missing-parent paths.
        var (dbContext, jobId) = await SeedSinglePendingJobAsync(AdvisorTurnJobType);
        var job = await dbContext.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);

        var now = DateTime.UtcNow;
        await JobBookkeeper.MarkFailedAsync(
            dbContext, job, now, "Job payload was not valid.", CancellationToken.None);

        var reloaded = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, reloaded.Status);
        Assert.Equal(now, reloaded.CompletedAt);
        Assert.Equal("Job payload was not valid.", reloaded.ErrorMessage);
        Assert.Equal(0, reloaded.AttemptCount);
    }

    [Fact]
    public async Task JobBookkeeper_RequeueOrFailAsync_BelowMaxAttempts_ResetsToPending()
    {
        // AttemptCount = 1 → next attempt = 2, below MaxAttempts (3). Status
        // goes back to Pending with StartedAt + CompletedAt cleared and
        // UpdatedAt = now so the next tick picks it up.
        var (dbContext, jobId) = await SeedSinglePendingJobAsync(AdvisorTurnJobType);
        var job = await dbContext.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.AttemptCount = 1;

        var now = DateTime.UtcNow;
        var outcome = await JobBookkeeper.RequeueOrFailAsync(
            dbContext, job, now, "transient LLM error", NullLogger.Instance, CancellationToken.None);

        Assert.Equal(JobBookkeeper.RequeueOrFailOutcome.Requeued, outcome);

        var reloaded = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Pending, reloaded.Status);
        Assert.Equal(2, reloaded.AttemptCount);
        Assert.Null(reloaded.StartedAt);
        Assert.Null(reloaded.CompletedAt);
        Assert.Equal("transient LLM error", reloaded.ErrorMessage);
        Assert.Equal(now, reloaded.UpdatedAt);
    }

    [Fact]
    public async Task JobBookkeeper_RequeueOrFailAsync_AtMaxAttempts_MovesToFailed()
    {
        // AttemptCount = 2 → next attempt = 3, hits MaxAttempts. Status
        // transitions to Failed, CompletedAt = now, ErrorMessage preserved.
        var (dbContext, jobId) = await SeedSinglePendingJobAsync(AdvisorTurnJobType);
        var job = await dbContext.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.AttemptCount = JobBookkeeper.MaxAttempts - 1;

        var now = DateTime.UtcNow;
        var outcome = await JobBookkeeper.RequeueOrFailAsync(
            dbContext, job, now, "transient LLM error", NullLogger.Instance, CancellationToken.None);

        Assert.Equal(JobBookkeeper.RequeueOrFailOutcome.Failed, outcome);

        var reloaded = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, reloaded.Status);
        Assert.Equal(JobBookkeeper.MaxAttempts, reloaded.AttemptCount);
        Assert.Equal(now, reloaded.CompletedAt);
        Assert.Equal("transient LLM error", reloaded.ErrorMessage);
    }

    // ----- infrastructure -----

    private static async Task<(WriteDbContext DbContext, Guid JobId)> SeedSinglePendingJobAsync(string jobType)
    {
        var databaseName = Guid.NewGuid().ToString();
        var sharedRoot = new InMemoryDatabaseRoot();
        var jobId = Guid.NewGuid();

        await using (var seedContext = CreateSystemScopedDbContext(databaseName, sharedRoot))
        {
            seedContext.Jobs.Add(new Job
            {
                Id = jobId,
                Type = jobType,
                PayloadJson = "{}",
                Status = JobStatus.Pending,
                AttemptCount = 0,
                AccountId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        return (CreateSystemScopedDbContext(databaseName, sharedRoot), jobId);
    }

    /// <summary>Same seed as <see cref="SeedSinglePendingJobAsync"/> but exposes
    /// the underlying database name + shared root so callers can build multiple
    /// independent <see cref="WriteDbContext"/> instances that share the same
    /// backing store — the only honest way to model two workers racing for
    /// the same row.</summary>
    private static async Task<(WriteDbContext DbContext, Guid JobId, string DatabaseName, InMemoryDatabaseRoot SharedRoot)>
        SeedSinglePendingJobSharedRootAsync(string jobType)
    {
        var databaseName = Guid.NewGuid().ToString();
        var sharedRoot = new InMemoryDatabaseRoot();
        var jobId = Guid.NewGuid();

        await using (var seedContext = CreateSystemScopedDbContext(databaseName, sharedRoot))
        {
            seedContext.Jobs.Add(new Job
            {
                Id = jobId,
                Type = jobType,
                PayloadJson = "{}",
                Status = JobStatus.Pending,
                AttemptCount = 0,
                AccountId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        return (CreateSystemScopedDbContext(databaseName, sharedRoot), jobId, databaseName, sharedRoot);
    }

    private static WriteDbContext CreateSystemScopedDbContext(
        string databaseName,
        InMemoryDatabaseRoot? sharedRoot = null)
    {
        var accountContext = new SystemAccountContext();
        var interceptor = new RowLevelSecurityInterceptor(accountContext);
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(databaseName, sharedRoot ?? new InMemoryDatabaseRoot())
            .AddInterceptors(interceptor)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new WriteDbContext(options, accountContext);
    }

    /// <summary><see cref="IAccountContext"/> with <c>IsAdministrator=true</c> so the
    /// seed write bypasses the global query filter — matches the system scope the
    /// production claim opens.</summary>
    private sealed class SystemAccountContext : IAccountContext
    {
        public Guid? UserId => null;
        public Guid? AccountId => null;
        public bool IsAdministrator => true;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
