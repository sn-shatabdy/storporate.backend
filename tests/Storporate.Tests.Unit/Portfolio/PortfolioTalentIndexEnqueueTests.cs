using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// STOR-43 Phase 1: pin the talent-index refresh enqueue contract from the
/// two portfolio touchpoints. The <see cref="PortfolioAnalysisJobProcessor"/>
/// enqueues a <see cref="TalentIndexJobTypes.RefreshEntry"/> job alongside
/// the Analyzed flip, and <see cref="DeletePortfolioItemHandler"/> enqueues
/// one alongside the item deletion. Both rely on
/// <see cref="TalentIndexRefreshJobs.EnqueueIfSearchableAsync"/> to do the
/// gate — non-searchable students must NOT get a refresh job (the processor
/// would just delete any prior entry, but the queue should stay shallow).
/// </summary>
/// <remarks>
/// These tests assert the enqueue happened (or didn't) by inspecting the
/// change tracker / Jobs DbSet after the operation, without driving the
/// processor itself. The processor-side coverage lives in
/// <c>RefreshTalentIndexEntryProcessorTests</c>; the gate itself lives in
/// <c>TalentIndexRefreshJobsTests</c>.
/// </remarks>
public class PortfolioTalentIndexEnqueueTests
{
    [Fact]
    public async Task DeletePortfolioItemHandler_SearchableStudent_EnqueuesTalentIndexRefreshJob()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        SeedProfile(dbContext, callerAccountId, isSearchable: true);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/x");

        await DeletePortfolioItemHandler.ExecuteAsync(
            item.Id, dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        var refreshJobs = await dbContext.Jobs.AsNoTracking()
            .Where(j => j.Type == TalentIndexJobTypes.RefreshEntry
                && j.AccountId == callerAccountId
                && j.Status == JobStatus.Pending)
            .ToListAsync();

        var job = Assert.Single(refreshJobs);
        Assert.Equal(callerAccountId, job.AccountId);

        var payload = System.Text.Json.JsonSerializer.Deserialize<RefreshTalentIndexPayload>(job.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(callerAccountId, payload!.StudentAccountId);
    }

    [Fact]
    public async Task DeletePortfolioItemHandler_NonSearchableStudent_DoesNotEnqueueTalentIndexRefreshJob()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        SeedProfile(dbContext, callerAccountId, isSearchable: false);

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/y");

        await DeletePortfolioItemHandler.ExecuteAsync(
            item.Id, dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        var refreshJobs = await dbContext.Jobs.AsNoTracking()
            .Where(j => j.Type == TalentIndexJobTypes.RefreshEntry
                && j.AccountId == callerAccountId)
            .ToListAsync();
        Assert.Empty(refreshJobs);
    }

    [Fact]
    public async Task DeletePortfolioItemHandler_StudentWithoutProfileRow_DoesNotEnqueueTalentIndexRefreshJob()
    {
        // A student who never toggled the feature has no StudentSearchProfile
        // row — same gate outcome as "opted out" for the enqueue helper's
        // purposes.
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/z");

        await DeletePortfolioItemHandler.ExecuteAsync(
            item.Id, dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        var refreshJobs = await dbContext.Jobs.AsNoTracking()
            .Where(j => j.Type == TalentIndexJobTypes.RefreshEntry
                && j.AccountId == callerAccountId)
            .ToListAsync();
        Assert.Empty(refreshJobs);
    }

    // ----- infrastructure -----

    private static (WriteDbContext DbContext, AmbientAccountContext AccountContext) CreateDbContext(Guid callerAccountId)
    {
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase("PortfolioTalentIndexEnqueueTests-" + Guid.NewGuid().ToString("N"))
            .Options;

        var accountContext = new AmbientAccountContext();
        accountContext.SetAccountId(callerAccountId);

        return (new WriteDbContext(options, accountContext), accountContext);
    }

    private static void SeedProfile(WriteDbContext dbContext, Guid accountId, bool isSearchable)
    {
        dbContext.StudentSearchProfiles.Add(new StudentSearchProfile
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            IsSearchable = isSearchable,
            DisplayName = isSearchable ? "Seed" : string.Empty,
            OptedInAt = isSearchable ? DateTimeOffset.UtcNow : null,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        dbContext.SaveChanges();
    }

    private static PortfolioItem SeedPortfolioItem(
        WriteDbContext dbContext,
        Guid accountId,
        string submissionType,
        string? storageKey = null,
        string? externalUrl = null)
    {
        var item = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Label = "Item",
            Category = PortfolioCategories.Document,
            SubmissionType = submissionType,
            StorageKey = storageKey,
            OriginalFileName = storageKey is null ? null : "report.pdf",
            ContentType = storageKey is null ? null : "application/pdf",
            FileSizeBytes = storageKey is null ? null : 100L,
            ExternalUrl = externalUrl,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PortfolioItems.Add(item);
        dbContext.SaveChanges();
        return item;
    }
}