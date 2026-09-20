using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.Modules.Portfolio.Analysis;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Jobs;

/// <summary>
/// STOR-40 Phase 1: pin the per-job account scope bracket the
/// <see cref="PortfolioAnalysisJobProcessor"/> now uses. The processor must (a) claim
/// any account's <c>Pending</c> job from the system scope and (b) re-enter the
/// owning account's scope before reading the parent item or writing findings, so the
/// EF Core global query filter and the save-time <c>RowLevelSecurityInterceptor</c>
/// key on the right account — no cross-account leakage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this test class exists in addition to <c>PortfolioAnalysisJobProcessorTests</c>.</b>
/// The existing suite exercises the success / retry / unsupported / concurrent-tick
/// flows; this suite is the cross-account isolation layer. Phase 1's specific bug
/// was a hand-rolled <see cref="IAccountContext"/> that the processor used during
/// its claim step — which meant the EF global query filter hid every job, leaving
/// the queue permanently empty. The fix wraps the processor in a real
/// <see cref="IBackgroundAccountScope"/> so the ambient context is populated
/// correctly across the claim + post-claim phases.
/// </para>
/// <para>
/// <b>What this test class doesn't cover.</b> The Postgres-side RLS policy is
/// exercised by the <c>account_scoped</c> follow-up migration's own SQL. The
/// query-filter pin per tenant entity lives in
/// <c>StudentGrowthTenantIsolationTests</c>. This suite only exercises the
/// worker's per-tick scope bracket using the production processor end-to-end.
/// </para>
/// </remarks>
public class BackgroundJobWorkerScopeTests
{
    [Fact]
    public async Task TryProcessOneAsync_TwoAccountsEachWithOnePendingJob_ClaimsExactlyOneAndStaysUnderOwningScope()
    {
        // Two accounts each have one Pending job + one fresh PortfolioItem. With the
        // pre-Phase-1 hand-rolled scope, the claim SELECT would have hidden both
        // rows behind the global query filter and the processor would have returned
        // NoWork. With the Phase-1 BeginSystemScope / BeginAccountScope bracket, one
        // tick claims one account's job and writes that account's findings — the
        // other account's rows are untouched.
        var databaseName = Guid.NewGuid().ToString();
        var sharedRoot = new InMemoryDatabaseRoot();

        var accountA = Guid.NewGuid();
        var portfolioItemA = Guid.NewGuid();
        var jobA = Guid.NewGuid();

        var accountB = Guid.NewGuid();
        var portfolioItemB = Guid.NewGuid();
        var jobB = Guid.NewGuid();

        await SeedAsync(databaseName, sharedRoot, accountA, portfolioItemA, jobA, "item-A");
        await SeedAsync(databaseName, sharedRoot, accountB, portfolioItemB, jobB, "item-B");

        var llmClient = new FakeLlmClient();
        llmClient.EnqueueResponse(SkillsJson(("React", ConfidenceBands.Strong, "A-side skill")));

        var processor = CreateProcessor(databaseName, sharedRoot, llmClient);

        var outcome = await processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        Assert.Equal(1, llmClient.CallCount);

        // One of the two jobs has reached a terminal state — the other is still
        // Pending, untouched. We don't pin which one (the InMemory claim is
        // CreatedAt-ordered and tied to whichever account seeded first chronologically,
        // but with two freshly-inserted rows the order is implementation-defined).
        await using var verifyContext = CreateSystemScopedDbContext(databaseName, sharedRoot);
        var jobs = await verifyContext.Jobs.AsNoTracking().OrderBy(j => j.CreatedAt).ToListAsync();
        var terminalJobs = jobs.Where(j => j.Status == JobStatus.Succeeded).ToList();
        var stillPending = jobs.Where(j => j.Status == JobStatus.Pending).ToList();

        Assert.Single(terminalJobs);
        Assert.Single(stillPending);

        // The findings row, if any, must belong to the same account as the job that
        // was processed. This is the headline invariant: cross-account write leakage
        // is what the worker-scope fix exists to prevent.
        var findings = await verifyContext.PortfolioSkillFindings.AsNoTracking().ToListAsync();
        if (findings.Count > 0)
        {
            var terminalJob = terminalJobs[0];
            Assert.All(findings, f => Assert.Equal(terminalJob.AccountId, f.AccountId));
        }
    }

    [Fact]
    public async Task TryProcessOneAsync_AbandonedJobFailedByReaper_OnJobAbandonedOnlyTouchesOwningAccountsItem()
    {
        // The reaper runs the system scope, so OnJobAbandonedAsync must be invoked
        // under the owning account's scope. Mirror that here: after the reaper
        // moves job A to Failed, the processor's OnJobAbandonedAsync flips A's item
        // to Failed and leaves B's item alone.
        var databaseName = Guid.NewGuid().ToString();
        var sharedRoot = new InMemoryDatabaseRoot();

        var accountA = Guid.NewGuid();
        var portfolioItemA = Guid.NewGuid();
        var jobA = Guid.NewGuid();

        var accountB = Guid.NewGuid();
        var portfolioItemB = Guid.NewGuid();

        await SeedAsync(databaseName, sharedRoot, accountA, portfolioItemA, jobA, "item-A");
        await SeedAsync(databaseName, sharedRoot, accountB, portfolioItemB, Guid.NewGuid(), "item-B");

        // Force job A to Running + past the reaper's window by mutating the row.
        await using (var bumpContext = CreateSystemScopedDbContext(databaseName, sharedRoot))
        {
            var jobAEntity = await bumpContext.Jobs.SingleAsync(j => j.Id == jobA);
            jobAEntity.Status = JobStatus.Running;
            jobAEntity.AttemptCount = StaleJobReaper.MaxAttemptsForReaper - 1;
            jobAEntity.UpdatedAt = DateTime.UtcNow - TimeSpan.FromMinutes(15);
            jobAEntity.StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(15);
            await bumpContext.SaveChangesAsync();
        }

        var processor = CreateProcessor(databaseName, sharedRoot, new FakeLlmClient());

        Job job;
        await using (var readContext = CreateSystemScopedDbContext(databaseName, sharedRoot))
        {
            job = await readContext.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobA);
        }

        await processor.OnJobAbandonedAsync(job, CancellationToken.None);

        await using var verifyContext = CreateSystemScopedDbContext(databaseName, sharedRoot);
        var items = await verifyContext.PortfolioItems.AsNoTracking().OrderBy(i => i.Label).ToListAsync();

        var itemA = items.Single(i => i.Label == "item-A");
        var itemB = items.Single(i => i.Label == "item-B");

        Assert.Equal(PortfolioAnalysisStatuses.Failed, itemA.AnalysisStatus);
        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, itemB.AnalysisStatus);
    }

    // ----- infrastructure -----

    private static PortfolioAnalysisJobProcessor CreateProcessor(
        string databaseName,
        InMemoryDatabaseRoot sharedRoot,
        FakeLlmClient llmClient)
    {
        var dbContext = CreateSystemScopedDbContext(databaseName, sharedRoot);
        var ambient = new AmbientAccountContext();
        var scope = new BackgroundAccountScope(ambient, ambient);
        var extractor = new EvidenceContentExtractor(
            new FakeArtifactStore(),
            NullLogger<EvidenceContentExtractor>.Instance);
        return new PortfolioAnalysisJobProcessor(
            dbContext,
            llmClient,
            extractor,
            NullLogger<PortfolioAnalysisJobProcessor>.Instance,
            TimeProvider.System,
            scope);
    }

    private static async Task SeedAsync(
        string databaseName,
        InMemoryDatabaseRoot sharedRoot,
        Guid accountId,
        Guid portfolioItemId,
        Guid jobId,
        string itemLabel)
    {
        await using var dbContext = CreateSystemScopedDbContext(databaseName, sharedRoot);
        dbContext.PortfolioItems.Add(new PortfolioItem
        {
            Id = portfolioItemId,
            AccountId = accountId,
            Label = itemLabel,
            Category = PortfolioCategories.PortfolioLink,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = "https://example.com/x",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        dbContext.Jobs.Add(new Job
        {
            Id = jobId,
            Type = PortfolioJobTypes.AnalyzePortfolioItem,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new AnalyzePortfolioItemPayload(portfolioItemId)),
            Status = JobStatus.Pending,
            AttemptCount = 0,
            AccountId = accountId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private static WriteDbContext CreateSystemScopedDbContext(string databaseName, InMemoryDatabaseRoot sharedRoot)
    {
        var accountContext = new SystemAccountContext();
        var interceptor = new RowLevelSecurityInterceptor(accountContext);
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(databaseName, sharedRoot)
            .AddInterceptors(interceptor)
            .Options;
        return new WriteDbContext(options, accountContext);
    }

    private static LlmCompletionResult SkillsJson(params (string Skill, string Band, string Explanation)[] skills)
    {
        var dtoArray = skills.Select(s => new PortfolioSkillFindingDto(s.Skill, s.Band, s.Explanation)).ToArray();
        var json = System.Text.Json.JsonSerializer.Serialize(dtoArray);
        return new LlmCompletionResult(json, ModelUsed: "test-model");
    }

    /// <summary><see cref="IAccountContext"/> with <c>IsAdministrator=true</c> so the
    /// seed write bypasses the global query filter — matches the system scope the
    /// production worker opens for the claim step.</summary>
    private sealed class SystemAccountContext : IAccountContext
    {
        public Guid? UserId => null;
        public Guid? AccountId => null;
        public bool IsAdministrator => true;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}