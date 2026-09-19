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

        // Seed an item belonging to a different account. The save interceptor
        // and global query filter run against the ambient AccountId at every
        // read/write, so the row is created under the other tenant — and the
        // handler's lookup (filtered to the caller's account) can't see it.
        var item = SeedPortfolioItem(
            dbContext,
            otherAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/theirs");

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

    [Fact]
    public async Task ExecuteAsync_AnalyzedItemWithTwoFindings_RemovesFindingsAndItemTogether()
    {
        // STOR-39 regression: with DeleteBehavior.Restrict on the PortfolioSkillFindings
        // FK to PortfolioItems, deleting an analyzed item used to leave its findings
        // pointing at the soon-to-be-deleted row and fail at SaveChanges. The fix must
        // RemoveRange the item's findings before Remove(item) inside the same
        // SaveChangesAsync so EF orders child deletes before the parent.
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/x");
        SeedSkillFinding(dbContext, callerAccountId, item.Id, "React", ConfidenceBands.Strong);
        SeedSkillFinding(dbContext, callerAccountId, item.Id, "Public Speaking", ConfidenceBands.Developing);

        var result = await DeletePortfolioItemHandler.ExecuteAsync(
            item.Id, dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        Assert.True(result);
        Assert.Empty(await dbContext.PortfolioItems.ToListAsync());
        Assert.Empty(await dbContext.PortfolioSkillFindings.ToListAsync());
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("portfolio_item_deleted", entry.Action);
        Assert.Equal(item.Id.ToString(), entry.ResourceId);
    }

    [Fact]
    public async Task ExecuteAsync_DeletingOneItem_LeavesAnotherItemsFindingsUntouched()
    {
        // Sibling-isolation: a delete scoped by PortfolioItemId must not touch findings
        // belonging to OTHER items in the same account. Same-account scope only — the
        // cross-account case is covered by the test below.
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        var target = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/target");
        SeedSkillFinding(dbContext, callerAccountId, target.Id, "TargetSkill", ConfidenceBands.Strong);

        var sibling = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/sibling");
        SeedSkillFinding(dbContext, callerAccountId, sibling.Id, "SiblingSkill", ConfidenceBands.Strong);

        var result = await DeletePortfolioItemHandler.ExecuteAsync(
            target.Id, dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        Assert.True(result);

        // Item deletion targeted only `target`.
        var remainingItems = await dbContext.PortfolioItems.ToListAsync();
        Assert.DoesNotContain(remainingItems, i => i.Id == target.Id);
        Assert.Contains(remainingItems, i => i.Id == sibling.Id);

        // Sibling's finding must survive — the fix scopes the delete by PortfolioItemId,
        // never by AccountId alone, so a sibling's findings don't get caught in the blast.
        var remainingFindings = await dbContext.PortfolioSkillFindings.ToListAsync();
        Assert.DoesNotContain(remainingFindings, f => f.PortfolioItemId == target.Id);
        Assert.Single(remainingFindings);
        Assert.Equal(sibling.Id, Assert.Single(remainingFindings).PortfolioItemId);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotLeakOtherAccountsFindingsAcrossSharedStore()
    {
        // Cross-account guard mirroring the ListPortfolioItemsHandlerTests pattern:
        // both accounts' rows live in the SAME shared InMemoryDatabaseRoot so the
        // global query filter is what keeps them apart, not per-test database
        // isolation. The delete must only remove findings belonging to the target
        // item under the caller's account.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        var sharedDatabaseName = Guid.NewGuid().ToString();

        // Seed A's analyzed item with one finding under A's ambient.
        var target = SeedItemAndFindingInSharedStore(
            sharedDatabaseName, accountA, "A's target item", "A's target skill");

        // Seed B's analyzed item with one finding under B's ambient using the
        // same shared store so the delete-time filter is the only isolation.
        SeedItemAndFindingInSharedStore(sharedDatabaseName, accountB, "B's item", "B's skill");

        var readDbContext = CreateSharedDbContext(sharedDatabaseName, accountA);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await DeletePortfolioItemHandler.ExecuteAsync(
            target.Id, readDbContext, artifactStore, auditLogWriter, CancellationToken.None);

        Assert.True(result);

        // Re-open under A and confirm: A's item is gone, B's item + B's finding survive.
        var verifyDbContext = CreateSharedDbContext(sharedDatabaseName, accountA);
        // The verify context can only see A's rows; to assert B's row count we open
        // under B's ambient. Same shape as ListPortfolioItemsHandlerTests uses.
        var verifyDbContextAsB = CreateSharedDbContext(sharedDatabaseName, accountB);

        Assert.Empty(await verifyDbContext.PortfolioItems.ToListAsync());
        Assert.Single(await verifyDbContextAsB.PortfolioItems.ToListAsync());
        Assert.Empty(await verifyDbContext.PortfolioSkillFindings.ToListAsync());
        Assert.Single(await verifyDbContextAsB.PortfolioSkillFindings.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_NotAnalyzedItem_DeletesWithoutTouchingFindings()
    {
        // Regression: zero-findings path must keep working unchanged. An item that
        // never went through analysis has no PortfolioSkillFinding rows, so the
        // RemoveRange is a no-op — the test asserts the row is deleted and no
        // exception is thrown by the (now-added) findings-load step.
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        var artifactStore = new FakeArtifactStore();
        var auditLogWriter = new FakeAuditLogWriter();

        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            PortfolioSubmissionTypes.Link,
            externalUrl: "https://example.com/x");

        var result = await DeletePortfolioItemHandler.ExecuteAsync(
            item.Id, dbContext, artifactStore, auditLogWriter, CancellationToken.None);

        Assert.True(result);
        Assert.Empty(await dbContext.PortfolioItems.ToListAsync());
        Assert.Empty(await dbContext.PortfolioSkillFindings.ToListAsync());
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("portfolio_item_deleted", entry.Action);
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

    /// <summary>
    /// Seeds a <see cref="PortfolioItem"/> plus one <see cref="PortfolioSkillFinding"/>
    /// in the shared in-memory database named <paramref name="databaseName"/>, against
    /// <paramref name="accountId"/>'s ambient context. Mirrors the
    /// <see cref="ListPortfolioItemsHandlerTests"/> pattern so cross-account isolation
    /// is exercised by the query filter, not by per-test database separation.
    /// </summary>
    private static PortfolioItem SeedItemAndFindingInSharedStore(
        string databaseName,
        Guid accountId,
        string itemLabel,
        string skillName)
    {
        var ambient = new AmbientAccountContext();
        ambient.SetAccountId(accountId);

        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(databaseName, CrossTestSharedInMemoryRoot)
            .Options;

        using var dbContext = new WriteDbContext(options, ambient);

        var item = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Label = itemLabel,
            Category = PortfolioCategories.Document,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = "https://example.com/x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PortfolioItems.Add(item);

        dbContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PortfolioItemId = item.Id,
            SkillName = skillName,
            ConfidenceBand = ConfidenceBands.Strong,
            Explanation = $"Explanation for {skillName}.",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        dbContext.SaveChanges();
        return item;
    }

    private static WriteDbContext CreateSharedDbContext(string databaseName, Guid callerAccountId)
    {
        var ambient = new AmbientAccountContext();
        ambient.SetAccountId(callerAccountId);

        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(databaseName, CrossTestSharedInMemoryRoot)
            .Options;

        return new WriteDbContext(options, ambient);
    }

    // Same shared InMemoryDatabaseRoot pattern ListPortfolioItemsHandlerTests uses:
    // one root per test class so the seed-context and the read-context (potentially
    // under a different ambient AccountId) see the same store. Each test still uses
    // a unique database name so test parallelism stays safe.
    private static readonly Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot CrossTestSharedInMemoryRoot = new();
}