using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Persistence;

/// <summary>
/// STOR-40 Phase 1: pin each new tenant entity's participation in the EF Core global
/// query filter (the first layer of the three-layer isolation pipeline) by exercising
/// the production <see cref="WriteDbContext"/> end-to-end against the in-memory provider.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the established per-entity query-filter pattern in
/// <c>PortfolioSkillFindingTenantIsolationTests</c> — one test per new
/// <see cref="IAccountScoped"/> table. Each test seeds a row under two different
/// accounts and reads under account A's ambient context; the global query filter must
/// hide B's row from A's read.
/// </para>
/// <para>
/// <b>What's NOT covered here.</b> The Postgres RLS policy is exercised by the
/// <c>account_scoped</c> follow-up migration's own SQL and pinned by the build-failing
/// <c>AccountScopedRowLevelSecurityTests</c> in the architecture suite. The
/// save-time <see cref="RowLevelSecurityInterceptor"/> guard is covered by
/// <c>RowLevelSecurityInterceptorTests</c>. This file pins the EF-side query filter
/// only.
/// </para>
/// <para>
/// <b>What about <see cref="FeedItem"/>.</b> Deliberately not covered here: it is a
/// global, non-<see cref="IAccountScoped"/> table — every reader sees the same feed.
/// The architecture test <c>AccountScopedRowLevelSecurityTests</c> asserts no
/// <c>CREATE POLICY account_scoped</c> is emitted for it.
/// </para>
/// </remarks>
public class StudentGrowthTenantIsolationTests
{
    [Fact]
    public async Task QueryFilter_Explorations_ReturnsOnlySameAccountRows()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        await SeedExplorationAsync(databaseName, accountA, "exploration-A");
        await SeedExplorationAsync(databaseName, accountB, "exploration-B");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
        });

        var visible = await dbContext.Explorations.AsNoTracking().ToListAsync();

        Assert.Single(visible);
        Assert.Equal(accountA, visible[0].AccountId);
        Assert.Equal("exploration-A", visible[0].Title);
    }

    [Fact]
    public async Task QueryFilter_ExplorationMessages_ReturnsOnlySameAccountRows()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        var explorationIdA = await SeedExplorationAsync(databaseName, accountA, "exploration-A");
        var explorationIdB = await SeedExplorationAsync(databaseName, accountB, "exploration-B");
        await SeedExplorationMessageAsync(databaseName, accountA, explorationIdA, "message-A");
        await SeedExplorationMessageAsync(databaseName, accountB, explorationIdB, "message-B");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
        });

        var visible = await dbContext.ExplorationMessages.AsNoTracking().ToListAsync();

        Assert.Single(visible);
        Assert.Equal(accountA, visible[0].AccountId);
        Assert.Equal("message-A", visible[0].Content);
    }

    [Fact]
    public async Task QueryFilter_ExplorationSummaryVersions_ReturnsOnlySameAccountRows()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        var explorationIdA = await SeedExplorationAsync(databaseName, accountA, "exploration-A");
        var explorationIdB = await SeedExplorationAsync(databaseName, accountB, "exploration-B");
        await SeedExplorationSummaryVersionAsync(databaseName, accountA, explorationIdA);
        await SeedExplorationSummaryVersionAsync(databaseName, accountB, explorationIdB);

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
        });

        var visible = await dbContext.ExplorationSummaryVersions.AsNoTracking().ToListAsync();

        Assert.Single(visible);
        Assert.Equal(accountA, visible[0].AccountId);
    }

    [Fact]
    public async Task QueryFilter_StudentContextNotes_ReturnsOnlySameAccountRows()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        await SeedStudentContextNoteAsync(databaseName, accountA, "note-A");
        await SeedStudentContextNoteAsync(databaseName, accountB, "note-B");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
        });

        var visible = await dbContext.StudentContextNotes.AsNoTracking().ToListAsync();

        Assert.Single(visible);
        Assert.Equal(accountA, visible[0].AccountId);
        Assert.Equal("note-A", visible[0].Text);
    }

    [Fact]
    public async Task QueryFilter_ExplorationComparisons_ReturnsOnlySameAccountRows()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        var explorationIdA = await SeedExplorationAsync(databaseName, accountA, "exploration-A");
        var explorationIdB = await SeedExplorationAsync(databaseName, accountB, "exploration-B");
        // A comparison needs two Explorations under its own account; reuse the same id
        // since the entity doesn't care whether the two sides differ.
        await SeedExplorationComparisonAsync(databaseName, accountA, explorationIdA);
        await SeedExplorationComparisonAsync(databaseName, accountB, explorationIdB);

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
        });

        var visible = await dbContext.ExplorationComparisons.AsNoTracking().ToListAsync();

        Assert.Single(visible);
        Assert.Equal(accountA, visible[0].AccountId);
    }

    [Fact]
    public async Task QueryFilter_StudentFeedEntries_ReturnsOnlySameAccountRows()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        await SeedStudentFeedEntryAsync(databaseName, accountA, "reason-A");
        await SeedStudentFeedEntryAsync(databaseName, accountB, "reason-B");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
        });

        var visible = await dbContext.StudentFeedEntries.AsNoTracking().ToListAsync();

        Assert.Single(visible);
        Assert.Equal(accountA, visible[0].AccountId);
        Assert.Equal("reason-A", visible[0].Reason);
    }

    // ----- per-entity seeders -----
    // Each seeder writes the row's FK parent (if any) before writing the row, so the
    // FK resolves cleanly under the row's owning account context. The seeder also
    // returns the seeded entity id where downstream seeders need it as an FK target.

    private static async Task<Guid> SeedExplorationAsync(string databaseName, Guid accountId, string title)
    {
        var id = Guid.NewGuid();
        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        });
        dbContext.Explorations.Add(new Exploration
        {
            Id = id,
            AccountId = accountId,
            Title = title,
            Status = ExplorationStatuses.Idle,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return id;
    }

    private static async Task SeedExplorationMessageAsync(
        string databaseName,
        Guid accountId,
        Guid explorationId,
        string content)
    {
        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        });
        dbContext.ExplorationMessages.Add(new ExplorationMessage
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExplorationId = explorationId,
            Role = ExplorationRoles.Student,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedExplorationSummaryVersionAsync(
        string databaseName,
        Guid accountId,
        Guid explorationId)
    {
        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        });
        dbContext.ExplorationSummaryVersions.Add(new ExplorationSummaryVersion
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExplorationId = explorationId,
            VersionNumber = 1,
            GapsJson = "[]",
            SuggestionsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedStudentContextNoteAsync(string databaseName, Guid accountId, string text)
    {
        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        });
        dbContext.StudentContextNotes.Add(new StudentContextNote
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Text = text,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedExplorationComparisonAsync(
        string databaseName,
        Guid accountId,
        Guid explorationId)
    {
        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        });
        dbContext.ExplorationComparisons.Add(new ExplorationComparison
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FirstExplorationId = explorationId,
            SecondExplorationId = explorationId,
            Status = ExplorationComparisonStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedStudentFeedEntryAsync(string databaseName, Guid accountId, string reason)
    {
        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        });
        var feedItem = new FeedItem
        {
            Id = Guid.NewGuid(),
            SourceName = "test-source",
            SourceUrl = $"https://example.com/{Guid.NewGuid():N}",
            Url = $"https://example.com/{Guid.NewGuid():N}",
            Title = $"item-{accountId:N}",
            Summary = "summary",
            FetchedAt = DateTimeOffset.UtcNow,
        };
        dbContext.FeedItems.Add(feedItem);

        dbContext.StudentFeedEntries.Add(new StudentFeedEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            FeedItemId = feedItem.Id,
            Reason = reason,
            MatchedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Build a <see cref="WriteDbContext"/> on the shared in-memory store with the
    /// production <see cref="RowLevelSecurityInterceptor"/>. Mirrors the pattern used
    /// by <c>RowLevelSecurityInterceptorTests</c> and
    /// <c>PortfolioSkillFindingTenantIsolationTests</c>.
    /// </summary>
    private static WriteDbContext CreateDbContext(string databaseName, IAccountContext accountContext)
    {
        var interceptor = new RowLevelSecurityInterceptor(accountContext);
        var dbContextOptions = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(databaseName, SharedInMemoryDatabaseRoot)
            .AddInterceptors(interceptor)
            .Options;
        return new WriteDbContext(dbContextOptions, accountContext);
    }

    // One InMemoryDatabaseRoot per test class so every DbContext built by this suite
    // shares the same in-memory store for a given name. Test parallelism is still safe
    // because each test uses its own Guid database name.
    private static readonly InMemoryDatabaseRoot SharedInMemoryDatabaseRoot = new();

    /// <summary>Plain property-bag test double for <see cref="IAccountContext"/>
    /// matching the shape every other suite in this repo uses for ambient-context
    /// tests.</summary>
    private sealed class TestAccountContext : IAccountContext
    {
        public Guid? UserId { get; init; }
        public Guid? AccountId { get; init; }
        public bool IsAdministrator { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
    }
}