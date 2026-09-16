using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Persistence;

/// <summary>
/// STOR-38 Phase 1: pin <see cref="PortfolioSkillFinding"/>'s participation in the
/// three-layer isolation pipeline by exercising the EF Core global query filter end-to-end
/// against the production <see cref="WriteDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a dedicated test class instead of adding cases to <c>RowLevelSecurityInterceptorTests</c>.</b>
/// That suite's job is to assert the interceptor's save-time guard against arbitrary
/// <see cref="IAccountScoped"/> entities; <see cref="PortfolioSkillFinding"/> is the first
/// new <see cref="IAccountScoped"/> entity since <c>Job</c> landed, so adding it there would
/// mix two orthogonal concerns. Keeping <see cref="IAccountScoped"/>-query-filter coverage
/// per entity in its own dedicated class makes future regressions locate the entity they
/// regressed against.
/// </para>
/// <para>
/// <b>Seed pattern.</b> Mirrors <c>RowLevelSecurityInterceptorTests</c> exactly: a per-test
/// unique in-memory database name plus a single <see cref="InMemoryDatabaseRoot"/> shared
/// across the seed context and the read context, so the seed's rows are visible to the
/// read even though each <c>DbContext</c> builds its own internal service provider. See
/// that test's <c>CreateDbContext</c> for the longer rationale; the same one applies here.
/// </para>
/// <para>
/// <b>What this test class doesn't cover.</b> The Postgres RLS policy itself is exercised
/// by the <c>account_scoped</c> migration's own SQL; this suite only pins the
/// EF-side query filter, matching the existing <c>RowLevelSecurityInterceptorTests</c>
/// coverage boundary. The integration test scaffolding would need a live Postgres to
/// exercise the policy, which the rest of the unit suite avoids on purpose.
/// </para>
/// </remarks>
public class PortfolioSkillFindingTenantIsolationTests
{
    [Fact]
    public async Task QueryFilter_AmbientAccountContext_ReturnsOnlySameAccountFindings()
    {
        // Two accounts each own one PortfolioItem + one PortfolioSkillFinding. The ambient
        // IAccountContext for the read resolves to account A; the global query filter must
        // hide B's finding from A's read — no explicit Where(e.AccountId == a) on the LINQ
        // side, the filter does the work.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        var databaseName = Guid.NewGuid().ToString();
        var portfolioItemIdA = await SeedFindingAsync(databaseName, accountA, accountA, "A's skill");
        await SeedFindingAsync(databaseName, accountB, accountB, "B's skill");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
        });

        var fromAccountA = await dbContext.PortfolioSkillFindings.ToListAsync();

        Assert.Single(fromAccountA);
        Assert.Equal(accountA, fromAccountA[0].AccountId);
        Assert.Equal("A's skill", fromAccountA[0].SkillName);
    }

    [Fact]
    public async Task QueryFilter_DifferentAmbientAccount_ReturnsOnlyThatAccountsFindings()
    {
        // Same shape as the first case but the ambient context flips to account B. The
        // filter must narrow to B's finding only — proves the filter re-binds to the
        // *current* ambient context rather than caching the first one.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        await SeedFindingAsync(databaseName, accountA, accountA, "A's skill");
        await SeedFindingAsync(databaseName, accountB, accountB, "B's skill");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountB,
            AccountId = accountB,
        });

        var visible = await dbContext.PortfolioSkillFindings.ToListAsync();

        Assert.Single(visible);
        Assert.Equal(accountB, visible[0].AccountId);
        Assert.Equal("B's skill", visible[0].SkillName);
    }

    [Fact]
    public async Task AdministratorQueryFilter_SeesAllAccountsFindings()
    {
        // Administrator bypasses the query filter via the OR clause
        // "e.AccountId == ambient || ambient.IsAdministrator". Two findings under
        // different accounts must both surface.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        await SeedFindingAsync(databaseName, accountA, accountA, "A's skill");
        await SeedFindingAsync(databaseName, accountB, accountB, "B's skill");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = Guid.NewGuid(),
            AccountId = null,
            IsAdministrator = true,
        });

        var allFindings = await dbContext.PortfolioSkillFindings.ToListAsync();

        Assert.Equal(2, allFindings.Count);
    }

    /// <summary>
    /// Seeds a <see cref="PortfolioItem"/> (FK target) under <paramref name="accountId"/>
    /// and a <see cref="PortfolioSkillFinding"/> pointing at it under the same account, so
    /// the seed itself doesn't trip the tenancy guard. Uses the same in-memory database
    /// name as the test that called it so both contexts see the same store.
    /// </summary>
    private static async Task<Guid> SeedFindingAsync(string databaseName, Guid accountId, Guid portfolioItemAccountId, string skillName)
    {
        var seedContext = new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        };

        await using var dbContext = CreateDbContext(databaseName, seedContext);

        // The seeded PortfolioItem must exist before the finding's FK can resolve. The
        // parent item is what carries the AccountId used by the FK; if seed callers
        // already created an item they can pass its id (caller overload intentionally
        // unused for simplicity — every seed call creates its own item to keep the
        // arrange step self-contained).
        var portfolioItem = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = portfolioItemAccountId,
            Label = $"item-{accountId:N}",
            Category = PortfolioCategories.Document,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = "https://example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PortfolioItems.Add(portfolioItem);

        var finding = new PortfolioSkillFinding
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PortfolioItemId = portfolioItem.Id,
            SkillName = skillName,
            ConfidenceBand = ConfidenceBands.Strong,
            Explanation = $"Explanation backing {skillName} for {accountId:N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PortfolioSkillFindings.Add(finding);

        await dbContext.SaveChangesAsync();
        return portfolioItem.Id;
    }

    private static WriteDbContext CreateDbContext(string databaseName, IAccountContext accountContext)
    {
        // Same pattern as RowLevelSecurityInterceptorTests: in-memory EF provider on a
        // per-test database name, with a SharedInMemoryDatabaseRoot so seed and read
        // contexts see the same store. The interceptor is added exactly as Program.cs's
        // AddDbContext would do via its factory overload, including the same scoped
        // lifetime reasoning.
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

    /// <summary>
    /// Plain property-bag test double for <see cref="IAccountContext"/> used in this
    /// suite. Matches RowLevelSecurityInterceptorTests's TestAccountContext shape exactly
    /// so the two suites stay consistent in how they model ambient context.
    /// </summary>
    private sealed class TestAccountContext : IAccountContext
    {
        public Guid? UserId { get; init; }

        public Guid? AccountId { get; init; }

        public bool IsAdministrator { get; init; }

        public string? IpAddress { get; init; }

        public string? UserAgent { get; init; }
    }
}
