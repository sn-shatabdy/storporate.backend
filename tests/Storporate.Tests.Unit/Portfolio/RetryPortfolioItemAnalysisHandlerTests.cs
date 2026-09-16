using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// TDD coverage for <see cref="RetryPortfolioItemAnalysisHandler"/>: the producer
/// path that re-queues an analysis job for a <see cref="PortfolioAnalysisStatuses.Failed"/>
/// item. The handler is the user-initiated twin of
/// <see cref="CreatePortfolioItemHandler"/>'s initial-enqueue path, so the tests
/// mirror the create-path handler test's shape but verify the state-machine gate
/// (only <c>Failed</c> is retryable) and the atomic reset + new-job write.
/// </summary>
public class RetryPortfolioItemAnalysisHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_FailedItem_ResetsStatusToNotAnalyzed_AndEnqueuesPendingJob()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            analysisStatus: PortfolioAnalysisStatuses.Failed,
            lastAnalyzedAt: null);

        var outcome = await RetryPortfolioItemAnalysisHandler.ExecuteAsync(
            item.Id, dbContext, TimeProvider.System, CancellationToken.None);

        Assert.True(outcome.Enqueued);
        Assert.Equal(item.Id, outcome.PortfolioItemId);
        Assert.NotEqual(Guid.Empty, outcome.NewJobId);

        var reloadedItem = await dbContext.PortfolioItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, reloadedItem.AnalysisStatus);
        Assert.Null(reloadedItem.LastAnalyzedAt);

        var newJob = await dbContext.Jobs.AsNoTracking().SingleAsync(j => j.Id == outcome.NewJobId);
        Assert.Equal(Storporate.Modules.Portfolio.Analysis.PortfolioJobTypes.AnalyzePortfolioItem, newJob.Type);
        Assert.Equal(JobStatus.Pending, newJob.Status);
        Assert.Equal(0, newJob.AttemptCount);
        Assert.Equal(callerAccountId, newJob.AccountId);

        var payload = System.Text.Json.JsonSerializer.Deserialize<Storporate.Modules.Portfolio.Analysis.AnalyzePortfolioItemPayload>(newJob.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(item.Id, payload!.PortfolioItemId);
    }

    [Fact]
    public async Task ExecuteAsync_AnalyzedItem_ThrowsPortfolioAnalysisNotRetryableException()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            analysisStatus: PortfolioAnalysisStatuses.Analyzed,
            lastAnalyzedAt: DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<PortfolioAnalysisNotRetryableException>(() =>
            RetryPortfolioItemAnalysisHandler.ExecuteAsync(item.Id, dbContext, TimeProvider.System, CancellationToken.None));

        Assert.Equal(PortfolioAnalysisStatuses.Analyzed, exception.ActualStatus);
        Assert.Contains("successfully", exception.Message);

        // The handler must NOT have mutated state — no new job should exist.
        Assert.Empty(await dbContext.Jobs.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_AnalyzingItem_ThrowsPortfolioAnalysisNotRetryableException()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            analysisStatus: PortfolioAnalysisStatuses.Analyzing,
            lastAnalyzedAt: null);

        var exception = await Assert.ThrowsAsync<PortfolioAnalysisNotRetryableException>(() =>
            RetryPortfolioItemAnalysisHandler.ExecuteAsync(item.Id, dbContext, TimeProvider.System, CancellationToken.None));

        Assert.Equal(PortfolioAnalysisStatuses.Analyzing, exception.ActualStatus);
        Assert.Contains("in progress", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NotAnalyzedItem_ThrowsPortfolioAnalysisNotRetryableException()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            analysisStatus: PortfolioAnalysisStatuses.NotAnalyzed,
            lastAnalyzedAt: null);

        var exception = await Assert.ThrowsAsync<PortfolioAnalysisNotRetryableException>(() =>
            RetryPortfolioItemAnalysisHandler.ExecuteAsync(item.Id, dbContext, TimeProvider.System, CancellationToken.None));

        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, exception.ActualStatus);
        Assert.Contains("queued", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedItem_ThrowsPortfolioAnalysisNotRetryableException()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            analysisStatus: PortfolioAnalysisStatuses.Unsupported,
            lastAnalyzedAt: null);

        var exception = await Assert.ThrowsAsync<PortfolioAnalysisNotRetryableException>(() =>
            RetryPortfolioItemAnalysisHandler.ExecuteAsync(item.Id, dbContext, TimeProvider.System, CancellationToken.None));

        Assert.Equal(PortfolioAnalysisStatuses.Unsupported, exception.ActualStatus);
        Assert.Contains("isn't supported", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_OtherAccountsItem_ReturnsEnqueuedFalse()
    {
        var callerAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        SeedPortfolioItem(
            dbContext,
            otherAccountId,
            analysisStatus: PortfolioAnalysisStatuses.Failed,
            lastAnalyzedAt: null);

        var outcome = await RetryPortfolioItemAnalysisHandler.ExecuteAsync(
            Guid.NewGuid(), dbContext, TimeProvider.System, CancellationToken.None);

        Assert.False(outcome.Enqueued);
        Assert.Empty(await dbContext.Jobs.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_UnknownId_ReturnsEnqueuedFalse()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var outcome = await RetryPortfolioItemAnalysisHandler.ExecuteAsync(
            Guid.NewGuid(), dbContext, TimeProvider.System, CancellationToken.None);

        Assert.False(outcome.Enqueued);
    }

    // ----- Test infrastructure below this line -----

    private static PortfolioItem SeedPortfolioItem(
        WriteDbContext dbContext,
        Guid accountId,
        string analysisStatus,
        DateTimeOffset? lastAnalyzedAt)
    {
        var item = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Label = "Item",
            Category = PortfolioCategories.Document,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = "https://example.com/x",
            CreatedAt = DateTimeOffset.UtcNow,
            AnalysisStatus = analysisStatus,
            LastAnalyzedAt = lastAnalyzedAt,
        };
        dbContext.PortfolioItems.Add(item);
        dbContext.SaveChanges();
        return item;
    }

    private static (WriteDbContext DbContext, AmbientAccountContext AccountContext) CreateDbContext(Guid callerAccountId)
    {
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var accountContext = new AmbientAccountContext();
        accountContext.SetUserId(callerAccountId);
        accountContext.SetAccountId(callerAccountId);

        return (new WriteDbContext(options, accountContext), accountContext);
    }
}
