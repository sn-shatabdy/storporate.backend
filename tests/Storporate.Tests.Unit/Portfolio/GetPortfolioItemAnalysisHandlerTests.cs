using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// TDD coverage for <see cref="GetPortfolioItemAnalysisHandler"/>: the read-side
/// of STOR-38 Phase 3. The handler is a thin orchestrator over the global query
/// filter plus a payload-deserialization lookup against the most-recent-failed
/// job, so the unit tests pin the four observable behaviors the integration
/// tests cover at the HTTP boundary: 200-with-empty-skills for an in-flight item,
/// 200-with-populated-skills for an analyzed item, 200-with-error-message for a
/// failed item, and null for a cross-account id.
/// </summary>
public class GetPortfolioItemAnalysisHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_CallersOwnNotAnalyzedItem_ReturnsEmptySkillsAndNoErrorMessage()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            analysisStatus: PortfolioAnalysisStatuses.NotAnalyzed,
            lastAnalyzedAt: null);

        var readModel = await GetPortfolioItemAnalysisHandler.ExecuteAsync(item.Id, dbContext, CancellationToken.None);

        Assert.NotNull(readModel);
        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, readModel!.Status);
        Assert.Null(readModel.LastAnalyzedAt);
        Assert.Null(readModel.ErrorMessage);
        Assert.Empty(readModel.Skills);
    }

    [Fact]
    public async Task ExecuteAsync_CallersOwnAnalyzedItem_ReturnsPopulatedSkillsAndLastAnalyzedAt()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var analyzedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            analysisStatus: PortfolioAnalysisStatuses.Analyzed,
            lastAnalyzedAt: analyzedAt);
        SeedSkillFinding(dbContext, callerAccountId, item.Id, "React", ConfidenceBands.Strong);
        SeedSkillFinding(dbContext, callerAccountId, item.Id, "Public Speaking", ConfidenceBands.Developing);

        var readModel = await GetPortfolioItemAnalysisHandler.ExecuteAsync(item.Id, dbContext, CancellationToken.None);

        Assert.NotNull(readModel);
        Assert.Equal(PortfolioAnalysisStatuses.Analyzed, readModel!.Status);
        Assert.Equal(analyzedAt, readModel.LastAnalyzedAt);
        Assert.Null(readModel.ErrorMessage);
        Assert.Equal(2, readModel.Skills.Count);
        Assert.Contains(readModel.Skills, s => s.SkillName == "React" && s.ConfidenceBand == ConfidenceBands.Strong);
        Assert.Contains(readModel.Skills, s => s.SkillName == "Public Speaking" && s.ConfidenceBand == ConfidenceBands.Developing);
    }

    [Fact]
    public async Task ExecuteAsync_CallersOwnFailedItem_ReturnsLatestFailureErrorMessage()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            analysisStatus: PortfolioAnalysisStatuses.Failed,
            lastAnalyzedAt: null);

        // Two failed jobs for the same item — the handler should surface the most
        // recent (by UpdatedAt) one. Seed an older one first so the ordering test
        // is meaningful.
        SeedFailedJob(dbContext, callerAccountId, item.Id, "older failure", updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        SeedFailedJob(dbContext, callerAccountId, item.Id, "newer failure", updatedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var readModel = await GetPortfolioItemAnalysisHandler.ExecuteAsync(item.Id, dbContext, CancellationToken.None);

        Assert.NotNull(readModel);
        Assert.Equal(PortfolioAnalysisStatuses.Failed, readModel!.Status);
        Assert.Null(readModel.LastAnalyzedAt);
        Assert.Equal("newer failure", readModel.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_FailedItemWithoutAnyFailedJob_ReturnsNullErrorMessage()
    {
        // Defensive: a Failed item with no Failed Job row (theoretical today,
        // but covers a future state-machine path that doesn't go through
        // FailJobAsync). The handler must not throw — it returns null error.
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            analysisStatus: PortfolioAnalysisStatuses.Failed,
            lastAnalyzedAt: null);

        var readModel = await GetPortfolioItemAnalysisHandler.ExecuteAsync(item.Id, dbContext, CancellationToken.None);

        Assert.NotNull(readModel);
        Assert.Null(readModel!.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_OtherAccountsItem_ReturnsNull()
    {
        var callerAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        SeedPortfolioItem(
            dbContext,
            otherAccountId,
            analysisStatus: PortfolioAnalysisStatuses.Analyzed,
            lastAnalyzedAt: DateTimeOffset.UtcNow);

        var readModel = await GetPortfolioItemAnalysisHandler.ExecuteAsync(Guid.NewGuid(), dbContext, CancellationToken.None);

        Assert.Null(readModel);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownId_ReturnsNull()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var readModel = await GetPortfolioItemAnalysisHandler.ExecuteAsync(Guid.NewGuid(), dbContext, CancellationToken.None);

        Assert.Null(readModel);
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

    private static void SeedSkillFinding(
        WriteDbContext dbContext,
        Guid accountId,
        Guid portfolioItemId,
        string skillName,
        string confidenceBand)
    {
        dbContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PortfolioItemId = portfolioItemId,
            SkillName = skillName,
            ConfidenceBand = confidenceBand,
            Explanation = $"Explanation for {skillName}.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        dbContext.SaveChanges();
    }

    private static void SeedFailedJob(
        WriteDbContext dbContext,
        Guid accountId,
        Guid portfolioItemId,
        string errorMessage,
        DateTimeOffset updatedAt)
    {
        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = Storporate.Modules.Portfolio.Analysis.PortfolioJobTypes.AnalyzePortfolioItem,
            Status = JobStatus.Failed,
            AttemptCount = Storporate.Modules.Portfolio.Analysis.PortfolioAnalysisJobProcessor.MaxAttempts,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new Storporate.Modules.Portfolio.Analysis.AnalyzePortfolioItemPayload(portfolioItemId)),
            ErrorMessage = errorMessage,
            AccountId = accountId,
            CreatedAt = updatedAt.AddMinutes(-5).UtcDateTime,
            UpdatedAt = updatedAt.UtcDateTime,
            StartedAt = updatedAt.AddMinutes(-4).UtcDateTime,
            CompletedAt = updatedAt.UtcDateTime,
        });
        dbContext.SaveChanges();
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
