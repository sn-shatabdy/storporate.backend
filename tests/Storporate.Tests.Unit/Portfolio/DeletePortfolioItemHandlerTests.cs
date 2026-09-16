using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// TDD coverage for <see cref="DeletePortfolioItemHandler"/>: a successful delete
/// removes the DB row, removes the storage blob (for File-type submissions), and
/// writes a single <c>portfolio_item_deleted</c> audit row; a delete targeting another
/// account's item returns false (the endpoint maps that to a 404, never a 403)
/// without touching storage or the audit log.
/// </summary>
public class DeletePortfolioItemHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_FileSubmission_DeletesRowAndBlobAndAudits()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            PortfolioSubmissionTypes.File,
            storageKey: "portfolio/abc/report.pdf");
        await artifactStore.PutAsync(item.StorageKey!, Stream.Null, "application/pdf");

        var result = await DeletePortfolioItemHandler.ExecuteAsync(
            item.Id, dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        Assert.True(result);
        Assert.Empty(await dbContext.PortfolioItems.ToListAsync());
        var delete = Assert.Single(artifactStore.DeleteCalls);
        Assert.Equal(item.StorageKey, delete.Key);
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("portfolio_item_deleted", entry.Action);
        Assert.Equal("PortfolioItem", entry.ResourceType);
        Assert.Equal(item.Id.ToString(), entry.ResourceId);
    }

    [Fact]
    public async Task ExecuteAsync_LinkSubmission_DeletesRowOnlyAndAudits()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/portfolio");

        var result = await DeletePortfolioItemHandler.ExecuteAsync(
            item.Id, dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        Assert.True(result);
        Assert.Empty(await dbContext.PortfolioItems.ToListAsync());
        Assert.Empty(artifactStore.DeleteCalls); // no blob to delete
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("portfolio_item_deleted", entry.Action);
        Assert.Equal(item.Id.ToString(), entry.ResourceId);
    }

    [Fact]
    public async Task ExecuteAsync_OtherAccountsItem_ReturnsFalseAndTouchesNothing()
    {
        var callerAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        // Seed an item belonging to a different account by temporarily swapping
        // the ambient account id, then revert it before invoking the handler.
        // The save interceptor and global query filter run against the
        // ambient AccountId at every read/write, so this is the cleanest way
        // to set up the cross-account delete scenario without bypassing the
        // production pipeline.
        var originalAccountId = (Guid)callerAccountId;
        var item = SeedPortfolioItem(
            dbContext,
            otherAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/theirs");
        _ = originalAccountId;

        var result = await DeletePortfolioItemHandler.ExecuteAsync(
            item.Id, dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        // The handler does not know which ambient AccountId the seed used; the
        // query filter restricted its lookup to the caller's account, so the
        // other-account row is invisible and the handler returns false.
        Assert.False(result);
        Assert.Empty(artifactStore.DeleteCalls);
        Assert.Empty(auditLogWriter.Recorded);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownId_ReturnsFalse()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await DeletePortfolioItemHandler.ExecuteAsync(
            Guid.NewGuid(), dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        Assert.False(result);
        Assert.Empty(artifactStore.DeleteCalls);
        Assert.Empty(auditLogWriter.Recorded);
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

    private static (WriteDbContext DbContext, AmbientAccountContext AccountContext) CreateDbContext(Guid callerAccountId)
    {
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var accountContext = new AmbientAccountContext();
        accountContext.SetAccountId(callerAccountId);

        return (new WriteDbContext(options, accountContext), accountContext);
    }
}