using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.Modules.Portfolio.Analysis;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Jobs;

/// <summary>
/// STOR-40 Phase 1: pin the <see cref="StaleJobReaper"/>'s three behaviors — a stale
/// <c>Running</c> job is re-queued as <c>Pending</c> with its attempt count bumped,
/// the cap of <see cref="StaleJobReaper.MaxAttemptsForReaper"/> trips the row to
/// <c>Failed</c>, and a job the reaper just touched is not reaped again until its
/// grace window elapses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated suite for the reaper.</b> The reaper runs under
/// <see cref="IBackgroundAccountScope.BeginSystemScope"/> and emits its own
/// <c>UPDATE</c> against <c>Jobs</c>; it does not need a processor or an LLM fake
/// to exercise. Putting it in its own suite keeps the arrange step small (seed a
/// <c>Running</c> row with an old <c>UpdatedAt</c>, advance the clock, assert
/// post-state) and stops the existing processor tests from drifting into
/// reaper-shaped assertions.
/// </para>
/// <para>
/// <b>Why <see cref="FakeTimeProvider"/>.</b> The reaper's stale-cutoff is computed
/// as <c>now - StaleRunningAfter</c> where <c>now</c> comes from the injected
/// <see cref="TimeProvider"/>. Using a controllable fake lets us pin the clock to
/// the exact instant we want without <c>Task.Delay</c> flakes; this is the same
/// pattern the existing processor tests use for deterministic DateTime comparisons.
/// </para>
/// <para>
/// <b>Why the InMemory branch.</b> The reaper code paths branch on
/// <c>Database.ProviderName?.Contains("InMemory")</c>; under that provider the
/// tracked-load-then-save path runs instead of the atomic Postgres
/// <see cref="RelationalCommandBuilderExtensions"/>. The reaper's behavior is the
/// same on both branches — the InMemory path here lets us pin the post-state
/// without standing up a live Postgres.
/// </para>
/// </remarks>
public class StaleJobReaperTests
{
    [Fact]
    public async Task RunOnceAsync_StaleRunningJobBelowCap_ResetsToPendingAndIncrementsAttempts()
    {
        // Arrange: a Running job whose UpdatedAt is 11 minutes old — past the default
        // 10-minute StaleRunningAfter window. AttemptCount = 1 means the reaper should
        // re-queue it as Pending (next attempt 2, below the 3-attempt cap).
        var (dbContext, accountId, jobId) = await SeedStaleRunningJobAsync(
            attemptCount: 1,
            staleFor: TimeSpan.FromMinutes(11));

        var timeProvider = new TimeProviderThatReturns(DateTimeOffset.UtcNow);
        var reaper = CreateReaper(dbContext, timeProvider);

        // Act
        var outcomes = await reaper.RunOnceAsync(CancellationToken.None);

        // Assert: the outcome carries the job at Pending, and the persisted row matches.
        Assert.Single(outcomes);
        Assert.Equal(JobStatus.Pending, outcomes[0].NextStatus);
        Assert.Equal(jobId, outcomes[0].Job.Id);

        var reloaded = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Pending, reloaded.Status);
        Assert.Equal(2, reloaded.AttemptCount);
        Assert.Null(reloaded.StartedAt);
        Assert.Null(reloaded.ErrorMessage);
        Assert.Null(reloaded.CompletedAt);
    }

    [Fact]
    public async Task RunOnceAsync_StaleRunningJobAtCap_MovesToFailedAndKeepsErrorMessage()
    {
        // AttemptCount = 2 → the reaper increments to 3, hits MaxAttemptsForReaper,
        // and flips Status to Failed with an "Job stopped responding" message.
        var (dbContext, _, jobId) = await SeedStaleRunningJobAsync(
            attemptCount: StaleJobReaper.MaxAttemptsForReaper - 1,
            staleFor: TimeSpan.FromMinutes(11));

        var timeProvider = new TimeProviderThatReturns(DateTimeOffset.UtcNow);
        var reaper = CreateReaper(dbContext, timeProvider);

        var outcomes = await reaper.RunOnceAsync(CancellationToken.None);

        Assert.Single(outcomes);
        Assert.Equal(JobStatus.Failed, outcomes[0].NextStatus);

        var reloaded = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, reloaded.Status);
        Assert.Equal(StaleJobReaper.MaxAttemptsForReaper, reloaded.AttemptCount);
        Assert.Equal("Job stopped responding", reloaded.ErrorMessage);
        Assert.NotNull(reloaded.CompletedAt);
        Assert.Equal(jobId, reloaded.Id);
    }

    [Fact]
    public async Task RunOnceAsync_RecentlyTouchedRunningJob_IsNotReaped()
    {
        // A Running job whose UpdatedAt is only 2 minutes old is well inside the
        // 10-minute stale window — the reaper must skip it entirely.
        var (dbContext, _, _) = await SeedStaleRunningJobAsync(
            attemptCount: 0,
            staleFor: TimeSpan.FromMinutes(2));

        var timeProvider = new TimeProviderThatReturns(DateTimeOffset.UtcNow);
        var reaper = CreateReaper(dbContext, timeProvider);

        var outcomes = await reaper.RunOnceAsync(CancellationToken.None);

        Assert.Empty(outcomes);
        var reloaded = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Running, reloaded.Status);
        Assert.Equal(0, reloaded.AttemptCount);
    }

    [Fact]
    public async Task RunOnceAsync_RunningJobsFromTwoAccounts_AreBothReaped()
    {
        // The reaper runs under the system scope so it must see every account's
        // Running jobs, not just the ambient one. Seed one job per account and
        // confirm both come back in the outcome list.
        var databaseName = Guid.NewGuid().ToString();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        var seedContext = CreateSystemScopedDbContext(databaseName);
        seedContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = PortfolioJobTypes.AnalyzePortfolioItem,
            PayloadJson = "{}",
            Status = JobStatus.Running,
            AttemptCount = 1,
            AccountId = accountA,
            UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(15),
            StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(15),
        });
        seedContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = PortfolioJobTypes.AnalyzePortfolioItem,
            PayloadJson = "{}",
            Status = JobStatus.Running,
            AttemptCount = 1,
            AccountId = accountB,
            UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(15),
            StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(15),
        });
        await seedContext.SaveChangesAsync();

        await using var readContext = CreateSystemScopedDbContext(databaseName);
        var timeProvider = new TimeProviderThatReturns(DateTimeOffset.UtcNow);
        var reaper = CreateReaper(readContext, timeProvider);

        var outcomes = await reaper.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.Contains(outcomes, o => o.Job.AccountId == accountA);
        Assert.Contains(outcomes, o => o.Job.AccountId == accountB);
    }

    [Fact]
    public async Task OnJobAbandonedAsync_RunningJobFailedByReaper_MirrorsAnalysisStatusOnParentItem()
    {
        // End-to-end: the reaper flips a stale AnalyzePortfolioItem job to Failed,
        // then OnJobAbandonedAsync mirrors Failed onto the parent PortfolioItem's
        // AnalysisStatus. This is the contract PortfolioAnalysisJobProcessor honors
        // under its own scope so the UI's "retry" button surfaces.
        var (dbContext, _, _) = await SeedStaleRunningJobAsync(
            attemptCount: StaleJobReaper.MaxAttemptsForReaper - 1,
            staleFor: TimeSpan.FromMinutes(11),
            submissionType: PortfolioSubmissionTypes.Link);

        var timeProvider = new TimeProviderThatReturns(DateTimeOffset.UtcNow);
        var reaper = CreateReaper(dbContext, timeProvider);

        var outcomes = await reaper.RunOnceAsync(CancellationToken.None);

        // The portfolio processor's OnJobAbandonedAsync is what the production worker
        // invokes after a Failed reaper outcome. We invoke it directly here under
        // the same account scope the worker would open, then assert the mirror
        // landed.
        Assert.Single(outcomes);
        var failedJob = outcomes[0].Job;

        var item = await dbContext.PortfolioItems.AsNoTracking().SingleAsync();
        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, item.AnalysisStatus);

        var processor = CreateProcessor(dbContext);
        await processor.OnJobAbandonedAsync(failedJob, CancellationToken.None);

        var refreshed = await dbContext.PortfolioItems.AsNoTracking().SingleAsync();
        Assert.Equal(PortfolioAnalysisStatuses.Failed, refreshed.AnalysisStatus);
        Assert.Null(refreshed.LastAnalyzedAt);
    }

    // ----- infrastructure -----

    /// <summary>
    /// Seeds one Running job linked to a freshly-created <see cref="PortfolioItem"/>,
    /// back-dated so its <c>UpdatedAt</c> lands <paramref name="staleFor"/> in the past.
    /// Returns the live <see cref="WriteDbContext"/> the test can pass to the reaper.
    /// </summary>
    private static async Task<(WriteDbContext DbContext, Guid AccountId, Guid JobId)> SeedStaleRunningJobAsync(
        int attemptCount,
        TimeSpan staleFor,
        string submissionType = PortfolioSubmissionTypes.Link)
    {
        var accountId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var portfolioItemId = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        // Seed under the system scope (IsAdministrator=true) so the seed write does
        // not trip the save-time tenancy guard. The reaper will run under the same
        // scope at tick time.
        var seedContext = CreateSystemScopedDbContext(databaseName);
        seedContext.PortfolioItems.Add(new PortfolioItem
        {
            Id = portfolioItemId,
            AccountId = accountId,
            Label = "item",
            Category = PortfolioCategories.PortfolioLink,
            SubmissionType = submissionType,
            ExternalUrl = "https://example.com/x",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seedContext.Jobs.Add(new Job
        {
            Id = jobId,
            Type = PortfolioJobTypes.AnalyzePortfolioItem,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new AnalyzePortfolioItemPayload(portfolioItemId)),
            Status = JobStatus.Running,
            AttemptCount = attemptCount,
            AccountId = accountId,
            CreatedAt = DateTime.UtcNow - staleFor - TimeSpan.FromMinutes(1),
            UpdatedAt = DateTime.UtcNow - staleFor,
            StartedAt = DateTime.UtcNow - staleFor,
        });
        await seedContext.SaveChangesAsync();

        return (CreateSystemScopedDbContext(databaseName), accountId, jobId);
    }

    private static WriteDbContext CreateSystemScopedDbContext(string databaseName)
    {
        var accountContext = new SystemAccountContext();
        var interceptor = new RowLevelSecurityInterceptor(accountContext);
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(databaseName, SharedRoot)
            .AddInterceptors(interceptor)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new WriteDbContext(options, accountContext);
    }

    private static StaleJobReaper CreateReaper(WriteDbContext dbContext, TimeProvider timeProvider) =>
        new(
            dbContext,
            processors: Array.Empty<IBackgroundJobProcessor>(),
            Options.Create(new BackgroundJobOptions()),
            NullLogger<StaleJobReaper>.Instance,
            timeProvider);

    private static PortfolioAnalysisJobProcessor CreateProcessor(WriteDbContext dbContext)
    {
        var ambient = new AmbientAccountContext();
        var scope = new BackgroundAccountScope(ambient, ambient);
        var extractor = new EvidenceContentExtractor(
            new FakeArtifactStore(),
            NullLogger<EvidenceContentExtractor>.Instance);
        return new PortfolioAnalysisJobProcessor(
            dbContext,
            llmClient: null!,
            extractor,
            NullLogger<PortfolioAnalysisJobProcessor>.Instance,
            TimeProvider.System,
            scope);
    }

    /// <summary>Deterministic clock for the reaper's cutoff comparison.</summary>
    private sealed class TimeProviderThatReturns : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public TimeProviderThatReturns(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary><see cref="IAccountContext"/> with <c>IsAdministrator=true</c> so the
    /// seed write bypasses the global query filter — the same scope the production
    /// reaper runs under.</summary>
    private sealed class SystemAccountContext : IAccountContext
    {
        public Guid? UserId => null;
        public Guid? AccountId => null;
        public bool IsAdministrator => true;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }

    private static readonly InMemoryDatabaseRoot SharedRoot = new();
}