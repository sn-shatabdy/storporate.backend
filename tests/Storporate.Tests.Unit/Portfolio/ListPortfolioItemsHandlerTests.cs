using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// TDD coverage for <see cref="ListPortfolioItemsHandler"/>: the EF InMemory provider
/// respects the <see cref="IEntityTypeConfiguration{HasQueryFilter}"/> lambda
/// that <see cref="WriteDbContext.OnModelCreating"/> installs for every
/// <see cref="IAccountScoped"/> entity, so seeding via the same
/// <see cref="WriteDbContext"/> is enough to verify the "list only returns the
/// caller's own items" acceptance criterion (the production
/// <see cref="AmbientAccountContext.AccountId"/> flows through the same filter
/// in both code paths).
/// </summary>
public class ListPortfolioItemsHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsOnlyTheCallersOwnItems()
    {
        var callerAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        SeedPortfolioItem(dbContext, callerAccountId, "Mine");
        SeedPortfolioItem(dbContext, otherAccountId, "Theirs");

        var request = new ListPortfolioItemsRequest { PageSize = 50 };

        var page = await ListPortfolioItemsHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("Mine", item.Label);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsEmptyResult_WhenCallerHasNoItems()
    {
        var callerAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        SeedPortfolioItem(dbContext, otherAccountId, "Theirs");

        var request = new ListPortfolioItemsRequest { PageSize = 50 };

        var page = await ListPortfolioItemsHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task ExecuteAsync_PaginatesWithSkipTake()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        for (var i = 0; i < 25; i++)
        {
            SeedPortfolioItem(dbContext, callerAccountId, $"Item-{i:D2}");
        }

        var firstPage = await ListPortfolioItemsHandler.ExecuteAsync(
            new ListPortfolioItemsRequest { PageNumber = 1, PageSize = 10 },
            dbContext,
            CancellationToken.None);

        var secondPage = await ListPortfolioItemsHandler.ExecuteAsync(
            new ListPortfolioItemsRequest { PageNumber = 2, PageSize = 10 },
            dbContext,
            CancellationToken.None);

        Assert.Equal(10, firstPage.Items.Count);
        Assert.Equal(10, secondPage.Items.Count);
        Assert.Equal(25, firstPage.TotalCount);
        Assert.DoesNotContain(firstPage.Items, i => secondPage.Items.Any(s => s.Id == i.Id));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSortKey_Throws()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        SeedPortfolioItem(dbContext, callerAccountId, "Item");

        var request = new ListPortfolioItemsRequest { SortBy = "not_a_real_key" };

        await Assert.ThrowsAsync<Storporate.Modules.Portfolio.UnknownSortKeyException>(() =>
            ListPortfolioItemsHandler.ExecuteAsync(request, dbContext, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsAnalysisStatusAndLastAnalyzedAt_OnEachRow()
    {
        // STOR-38 Phase 3 addendum: the list endpoint now carries the per-item
        // analysis state on every row so the Phase 5 portfolio page can render
        // its status badge without an extra API call per row. Seed two items
        // with different AnalysisStatus values and verify they round-trip into
        // the response unchanged.
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var analyzedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        SeedPortfolioItem(
            dbContext,
            callerAccountId,
            "Analyzed item",
            analysisStatus: PortfolioAnalysisStatuses.Analyzed,
            lastAnalyzedAt: analyzedAt);
        SeedPortfolioItem(
            dbContext,
            callerAccountId,
            "Pending item",
            analysisStatus: PortfolioAnalysisStatuses.NotAnalyzed,
            lastAnalyzedAt: null);

        var request = new ListPortfolioItemsRequest { PageSize = 50 };

        var page = await ListPortfolioItemsHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        Assert.Equal(2, page.Items.Count);

        var analyzedItem = Assert.Single(page.Items, i => i.Label == "Analyzed item");
        Assert.Equal(PortfolioAnalysisStatuses.Analyzed, analyzedItem.AnalysisStatus);
        Assert.Equal(analyzedAt, analyzedItem.LastAnalyzedAt);

        var pendingItem = Assert.Single(page.Items, i => i.Label == "Pending item");
        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, pendingItem.AnalysisStatus);
        Assert.Null(pendingItem.LastAnalyzedAt);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCondensedSkills_PopulatedForAnalyzedItems()
    {
        // STOR-39 Phase 1: the timeline view needs the per-item skill findings inline
        // (skill name + confidence-band color) so it can render one chip per finding
        // without an extra /analysis roundtrip per row. Seed an analyzed item with two
        // findings and assert both surface in the Skills array with the right
        // (skillName, confidenceBand) pair — Explanation never leaves the handler
        // because the timeline preview is deliberately condensed (the full evidence
        // text stays scoped to the per-item /analysis endpoint).
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var analyzedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var item = SeedPortfolioItem(
            dbContext,
            callerAccountId,
            "Analyzed item",
            analysisStatus: PortfolioAnalysisStatuses.Analyzed,
            lastAnalyzedAt: analyzedAt);
        SeedSkillFinding(dbContext, callerAccountId, item.Id, "React", ConfidenceBands.Strong);
        SeedSkillFinding(dbContext, callerAccountId, item.Id, "Public Speaking", ConfidenceBands.Developing);

        var request = new ListPortfolioItemsRequest { PageSize = 50 };

        var page = await ListPortfolioItemsHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        var response = Assert.Single(page.Items);
        Assert.Equal(2, response.Skills.Count);
        Assert.Contains(response.Skills, s => s.SkillName == "React" && s.ConfidenceBand == ConfidenceBands.Strong);
        Assert.Contains(response.Skills, s => s.SkillName == "Public Speaking" && s.ConfidenceBand == ConfidenceBands.Developing);
        // Defensive: PortfolioSkillPreview has no Explanation property to leak through —
        // confirm the runtime shape only knows about skillName + confidenceBand.
        Assert.Equal(typeof(string), typeof(PortfolioSkillPreview).GetProperty(nameof(PortfolioSkillPreview.SkillName))!.PropertyType);
        Assert.Equal(typeof(string), typeof(PortfolioSkillPreview).GetProperty(nameof(PortfolioSkillPreview.ConfidenceBand))!.PropertyType);
        Assert.Null(typeof(PortfolioSkillPreview).GetProperty("Explanation"));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsEmptySkillsArray_ForItemsWithNoFindings()
    {
        // STOR-39 Phase 1: a NotAnalyzed item has no PortfolioSkillFinding rows yet.
        // The response contract demands Skills be an empty array (never null) — the
        // frontend's "no skills yet" branch in the timeline view would otherwise need
        // an explicit ?? [] on every read site, same rationale GetPortfolioItemAnalysisResponse
        // already documents for its own Skills array.
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        SeedPortfolioItem(
            dbContext,
            callerAccountId,
            "Not analyzed",
            analysisStatus: PortfolioAnalysisStatuses.NotAnalyzed,
            lastAnalyzedAt: null);

        var request = new ListPortfolioItemsRequest { PageSize = 50 };

        var page = await ListPortfolioItemsHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        var response = Assert.Single(page.Items);
        Assert.NotNull(response.Skills);
        Assert.Empty(response.Skills);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsEmptyPageWithEmptyItemsArray_WhenCallerHasNoItems()
    {
        // Cross-checks the empty-page branch (totalCount == 0 short-circuit) — the
        // response's Items array must be empty (not null), matching the existing
        // PagedResult<T> contract.
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        var page = await ListPortfolioItemsHandler.ExecuteAsync(
            new ListPortfolioItemsRequest { PageSize = 50 },
            dbContext,
            CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotLeakOtherAccountsSkillFindings()
    {
        // STOR-39 Phase 1: the new bulk PortfolioSkillFindings query is scoped to the
        // caller's account by the same global query filter that protects the
        // PortfolioItem read above — even though the handler issues a Contains(pageItemIds)
        // predicate, an item id that matches a row under another account must be
        // invisible (filtered) before the predicate ever sees it. Seed two distinct
        // accounts each with one item + one finding; assert account A's list never
        // surfaces account B's skill name.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        // Important: both seed rows must live in the SAME in-memory database store
        // so the cross-account isolation predicate is what filters B out of A's read
        // (not the per-test database isolation). Share the database name across the
        // two DbContexts and swap the ambient account between them — same pattern
        // PortfolioSkillFindingTenantIsolationTests establishes.
        var sharedDatabaseName = Guid.NewGuid().ToString();

        // Seed A's item + finding under A's ambient context.
        var itemA = SeedItemAndFindingInSharedStore(sharedDatabaseName, accountA, "A's item", "A's skill");

        // Seed B's item + finding under B's ambient context using the same database.
        SeedItemAndFindingInSharedStore(sharedDatabaseName, accountB, "B's item", "B's skill");

        // Now read under A — the global query filter must hide B's finding.
        var readDbContext = CreateSharedDbContext(sharedDatabaseName, accountA);

        var page = await ListPortfolioItemsHandler.ExecuteAsync(
            new ListPortfolioItemsRequest { PageSize = 50 },
            readDbContext,
            CancellationToken.None);

        var responseA = Assert.Single(page.Items, i => i.Id == itemA.Id);
        Assert.Single(responseA.Skills);
        Assert.Equal("A's skill", responseA.Skills[0].SkillName);
        Assert.DoesNotContain(page.Items, i => i.Label == "B's item");
        Assert.Contains(responseA.Skills, s => s.SkillName == "A's skill");
        Assert.DoesNotContain(responseA.Skills, s => s.SkillName == "B's skill");
    }

    private static PortfolioItem SeedPortfolioItem(
        WriteDbContext dbContext,
        Guid accountId,
        string label,
        string analysisStatus = PortfolioAnalysisStatuses.NotAnalyzed,
        DateTimeOffset? lastAnalyzedAt = null)
    {
        var item = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Label = label,
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

    /// <summary>
    /// Seeds a <see cref="PortfolioItem"/> and one <see cref="PortfolioSkillFinding"/>
    /// pointed at it, inside the shared in-memory database named
    /// <paramref name="databaseName"/>, against the supplied
    /// <paramref name="accountId"/>'s ambient context. Used by the cross-account
    /// isolation test to put two tenants' rows in the same store, then read under
    /// one tenant's context — the global query filter is the only thing keeping
    /// them apart.
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
            AnalysisStatus = PortfolioAnalysisStatuses.Analyzed,
            LastAnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
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

    // One InMemoryDatabaseRoot shared across the cross-account isolation tests'
    // seed-context / read-context so multiple DbContexts on the same database name
    // see the same store. Each test uses a unique database name so parallelization
    // stays safe — same shared-root pattern PortfolioSkillFindingTenantIsolationTests
    // uses for the same reason.
    private static readonly Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot CrossTestSharedInMemoryRoot = new();

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