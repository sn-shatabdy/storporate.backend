using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.Modules.StudentGrowthExperience;
using Storporate.Modules.StudentGrowthExperience.Advisor;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.StudentGrowthExperience;

/// <summary>
/// Coverage for <see cref="CompareExplorationsJobProcessor"/>. Pinned
/// behaviors:
/// <list type="bullet">
///   <item>Happy path: comparison row moves from Pending → Completed with a non-empty result text.</item>
///   <item>Failure after 3 attempts: job Failed, comparison Status=Failed.</item>
///   <item>OnJobAbandonedAsync flips a Pending comparison to Failed.</item>
/// </list>
/// </summary>
public class CompareExplorationsJobProcessorTests
{
    [Fact]
    public async Task HappyPath_LlmReturnsReply_ComparisonIsCompletedWithResultText()
    {
        var fixture = await SeedPendingComparisonAsync();
        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: """{"reply":"Both directions overlap on teamwork."}""",
            ModelUsed: "test-model"));

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);

        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);

        var comparison = await fixture.Db.ExplorationComparisons.AsNoTracking()
            .SingleAsync(c => c.Id == fixture.ComparisonId);
        Assert.Equal(ExplorationComparisonStatuses.Completed, comparison.Status);
        Assert.NotNull(comparison.ResultText);
        Assert.False(string.IsNullOrWhiteSpace(comparison.ResultText));
    }

    [Fact]
    public async Task ThreeLlmFailures_JobAndComparisonBothEndAsFailed()
    {
        var fixture = await SeedPendingComparisonAsync();
        fixture.LlmClient.EnqueueException(new LlmProviderException("c1"));
        fixture.LlmClient.EnqueueException(new LlmProviderException("c2"));
        fixture.LlmClient.EnqueueException(new LlmProviderException("c3"));

        for (var i = 0; i < JobBookkeeper.MaxAttempts; i++)
        {
            var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
            Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        }

        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobBookkeeper.MaxAttempts, job.AttemptCount);

        var comparison = await fixture.Db.ExplorationComparisons.AsNoTracking()
            .SingleAsync(c => c.Id == fixture.ComparisonId);
        Assert.Equal(ExplorationComparisonStatuses.Failed, comparison.Status);
    }

    [Fact]
    public async Task OnJobAbandonedAsync_PendingComparison_FlipsToFailed()
    {
        var fixture = await SeedPendingComparisonAsync();
        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();

        await fixture.Processor.OnJobAbandonedAsync(job, CancellationToken.None);

        var comparison = await fixture.Db.ExplorationComparisons.AsNoTracking()
            .SingleAsync(c => c.Id == fixture.ComparisonId);
        Assert.Equal(ExplorationComparisonStatuses.Failed, comparison.Status);
    }

    // ----- infrastructure -----

    private async Task<Fixture> SeedPendingComparisonAsync()
    {
        var accountId = Guid.NewGuid();
        var comparisonId = Guid.NewGuid();
        var databaseName = "CompareExplorationsJobProcessorTests-" + Guid.NewGuid().ToString("N");
        var sharedRoot = new InMemoryDatabaseRoot();
        var llmClient = new FakeLlmClient();

        await using (var seedContext = CreateDbContextOnSharedRoot(
                         databaseName, sharedRoot, accountId))
        {
            // Two explorations + two summary versions + one comparison row.
            var explorationAId = Guid.NewGuid();
            var explorationBId = Guid.NewGuid();

            seedContext.Explorations.Add(new Exploration
            {
                Id = explorationAId,
                AccountId = accountId,
                Title = "A",
                Status = ExplorationStatuses.Idle,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            seedContext.Explorations.Add(new Exploration
            {
                Id = explorationBId,
                AccountId = accountId,
                Title = "B",
                Status = ExplorationStatuses.Idle,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            seedContext.ExplorationComparisons.Add(new ExplorationComparison
            {
                Id = comparisonId,
                AccountId = accountId,
                FirstExplorationId = explorationAId,
                SecondExplorationId = explorationBId,
                Status = ExplorationComparisonStatuses.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            seedContext.Jobs.Add(new Job
            {
                Id = Guid.NewGuid(),
                Type = GrowthJobTypes.CompareExplorations,
                PayloadJson = JsonSerializer.Serialize(new CompareExplorationsPayload(comparisonId)),
                Status = JobStatus.Pending,
                AttemptCount = 0,
                AccountId = accountId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            await seedContext.SaveChangesAsync();
        }

        var dbContext = CreateDbContextOnSharedRoot(databaseName, sharedRoot, accountId);
        var ambient = new AmbientAccountContext();
        var scope = new BackgroundAccountScope(ambient, ambient);
        var processor = new CompareExplorationsJobProcessor(
            dbContext,
            llmClient,
            Options.Create(new AdvisorOptions()),
            NullLogger<CompareExplorationsJobProcessor>.Instance,
            TimeProvider.System,
            scope);
        return new Fixture(dbContext, processor, llmClient, comparisonId);
    }

    private static WriteDbContext CreateDbContextOnSharedRoot(
        string databaseName,
        InMemoryDatabaseRoot sharedRoot,
        Guid accountId)
    {
        var accountContext = new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        };
        var interceptor = new RowLevelSecurityInterceptor(accountContext);
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(databaseName, sharedRoot)
            .AddInterceptors(interceptor)
            .Options;
        return new WriteDbContext(options, accountContext);
    }

    private sealed class TestAccountContext : IAccountContext
    {
        public Guid? UserId { get; init; }
        public Guid? AccountId { get; init; }
        public bool IsAdministrator { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
    }

    private sealed record Fixture(
        WriteDbContext Db,
        CompareExplorationsJobProcessor Processor,
        FakeLlmClient LlmClient,
        Guid ComparisonId);
}
