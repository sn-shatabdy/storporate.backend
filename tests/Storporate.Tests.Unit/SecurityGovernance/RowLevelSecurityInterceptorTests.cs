using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// Unit coverage for STOR-62 Phase 4: the EF Core global query filter installed in
/// <see cref="WriteDbContext.OnModelCreating"/> and the save-time tenancy guard in
/// <see cref="RowLevelSecurityInterceptor"/> working together to keep accounts isolated.
/// </summary>
/// <remarks>
/// <para>
/// Each test wires the production <see cref="WriteDbContext"/> against an in-memory EF
/// provider (matching the in-memory pattern this suite uses everywhere — see
/// <c>PermissionServiceTests</c>), backed by a fresh <see cref="TestAccountContext"/>,
/// and adds the production <see cref="RowLevelSecurityInterceptor"/> via
/// <see cref="DbContextOptionsBuilder.AddInterceptors(System.Collections.Generic.IEnumerable{Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor})"/>
/// exactly the way <c>Program.cs</c> does. The Postgres GUC calls inside the interceptor
/// are silently skipped under the in-memory provider (the connection type check returns
/// false), so the tests stay portable across providers.
/// </para>
/// <para>
/// The pre-existing identity/auth tests do not exercise <see cref="IAccountScoped"/>
/// entities, so the guard's "no IAccountScoped entries -&gt; no-op" short-circuit keeps
/// them unaffected.
/// </para>
/// </remarks>
public class RowLevelSecurityInterceptorTests
{
    [Fact]
    public async Task QueryFilter_AmbientAccountContext_ReturnsOnlySameAccountRows()
    {
        // Two accounts owning one Job each. Account A is the ambient context for the read,
        // account B is somebody else entirely. The global query filter must hide B's Job
        // from A's read — no explicit Where(e.AccountId == a) on the LINQ side, the filter
        // does the work.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        var databaseName = Guid.NewGuid().ToString();
        await SeedJobAsync(databaseName, accountA, "A's job");
        await SeedJobAsync(databaseName, accountB, "B's job");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
        });

        var fromAccountA = await dbContext.Jobs.ToListAsync();

        Assert.Single(fromAccountA);
        Assert.Equal(accountA, fromAccountA[0].AccountId);
    }

    [Fact]
    public async Task QueryFilter_DifferentAmbientAccount_ReturnsOnlyThatAccountsRows()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        await SeedJobAsync(databaseName, accountA, "A's job");
        await SeedJobAsync(databaseName, accountB, "B's job");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = accountB,
            AccountId = accountB,
        });

        var visible = await dbContext.Jobs.ToListAsync();

        Assert.Single(visible);
        Assert.Equal(accountB, visible[0].AccountId);
    }

    [Fact]
    public async Task SaveChanges_NewJobWithMismatchedAccountId_ThrowsInvalidOperationException()
    {
        var ambientAccount = Guid.NewGuid();
        var otherAccount = Guid.NewGuid();

        await using var dbContext = CreateDbContext(Guid.NewGuid().ToString(), new TestAccountContext
        {
            UserId = ambientAccount,
            AccountId = ambientAccount,
        });

        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = "test",
            PayloadJson = "{}",
            AccountId = otherAccount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        // The tenancy guard fires on SavingChangesAsync, so awaiting the assertion is the
        // exact production save path. The thrown InvalidOperationException must NOT carry
        // through as an UpdateException, since the interceptor returns the result unchanged
        // after throwing — the SaveChangesAsync itself is what propagates.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dbContext.SaveChangesAsync());

        Assert.Contains("account-scoped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveChanges_ModifiedJobWithMismatchedAccountId_ThrowsInvalidOperationException()
    {
        // Same shape as the Added case, but with a job that started out matching the
        // ambient account and then got its AccountId reassigned before SaveChanges. Proves
        // the Modified branch is also covered.
        var ambientAccount = Guid.NewGuid();
        var otherAccount = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        await SeedJobAsync(databaseName, ambientAccount, "Initial payload");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = ambientAccount,
            AccountId = ambientAccount,
        });

        var tracked = await dbContext.Jobs.SingleAsync();
        tracked.AccountId = otherAccount;
        tracked.PayloadJson = "tampered";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dbContext.SaveChangesAsync());

        Assert.Contains("account-scoped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveChanges_JobMatchingAmbientAccount_Succeeds()
    {
        // Negative-control to prove the guard doesn't false-positive: a Job whose
        // AccountId matches the ambient account must save cleanly.
        var ambientAccount = Guid.NewGuid();

        await using var dbContext = CreateDbContext(Guid.NewGuid().ToString(), new TestAccountContext
        {
            UserId = ambientAccount,
            AccountId = ambientAccount,
        });

        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = "test",
            PayloadJson = "{}",
            AccountId = ambientAccount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var saved = await dbContext.SaveChangesAsync();

        Assert.Equal(1, saved);
    }

    [Fact]
    public async Task AdministratorQueryFilter_SeesAllAccountsRows()
    {
        // Administrator bypasses the query filter via the OR clause
        // "e.AccountId == ambient || ambient.IsAdministrator". Two Jobs under different
        // accounts must both surface.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        await SeedJobAsync(databaseName, accountA, "A's job");
        await SeedJobAsync(databaseName, accountB, "B's job");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext
        {
            UserId = Guid.NewGuid(),
            AccountId = null,
            IsAdministrator = true,
        });

        var allJobs = await dbContext.Jobs.ToListAsync();

        Assert.Equal(2, allJobs.Count);
    }

    [Fact]
    public async Task AdministratorCanWriteJobToAnyAccountWithoutGuardThrowing()
    {
        // Administrator context must allow the tenancy guard to be skipped entirely:
        // writing a Job whose AccountId points at a different user does NOT throw.
        var adminUserId = Guid.NewGuid();
        var targetAccount = Guid.NewGuid();

        await using var dbContext = CreateDbContext(Guid.NewGuid().ToString(), new TestAccountContext
        {
            UserId = adminUserId,
            AccountId = null,
            IsAdministrator = true,
        });

        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = "admin-write",
            PayloadJson = "{}",
            AccountId = targetAccount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var saved = await dbContext.SaveChangesAsync();

        Assert.Equal(1, saved);
    }

    [Fact]
    public async Task QueryFilter_NoAmbientAccount_ReadsNothing()
    {
        // Defense-in-depth for the "no ambient account at all" case: with no AccountId
        // and no IsAdministrator, the filter resolves to
        // (e.AccountId == null && no admin flag) which never matches a non-null
        // AccountId on real jobs. The test exists to pin this behavior so future
        // refactors of the filter expression don't silently flip it.
        var accountA = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        await SeedJobAsync(databaseName, accountA, "A's job");

        await using var dbContext = CreateDbContext(databaseName, new TestAccountContext());

        var visible = await dbContext.Jobs.ToListAsync();

        Assert.Empty(visible);
    }

    [Fact]
    public void SavingChanges_NonAccountScopedChanges_DoesNotThrowEvenWithoutAmbient()
    {
        // The guard must short-circuit when there are no IAccountScoped entries in the
        // change tracker — saving Users/Sessions with no ambient account must remain
        // untouched by this interceptor, which is why the integration tests that seed
        // fixtures through SaveChangesAsync before any IAccountContext is populated keep
        // working.
        using var dbContext = CreateDbContext(Guid.NewGuid().ToString(), new TestAccountContext());

        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = "x@example.com",
            ActorType = ActorTypes.Student,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        // Must not throw — the guard short-circuits because Users are not IAccountScoped.
        dbContext.SaveChanges();
    }

    private static async Task SeedJobAsync(string databaseName, Guid accountId, string payload)
    {
        // Seeds a Job into a WriteDbContext that has an ambient context set to match
        // AccountId, so the seed itself doesn't trip the tenancy guard. Uses the same
        // in-memory database name as the test that called it so both contexts see the
        // same store.
        var seedContext = new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        };

        await using var dbContext = CreateDbContext(databaseName, seedContext);

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = "seed",
            PayloadJson = payload,
            AccountId = accountId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Jobs.Add(job);
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Plain property-bag test double for <see cref="IAccountContext"/> used in this
    /// suite. A plain non-AsyncLocal implementation is preferred here for clarity:
    /// each test fully owns the account context it constructs (init-only properties
    /// set at construction time), so there's no ambiguity about which values are
    /// visible to which DbContext instance. <see cref="AmbientAccountContext"/>'s
    /// AsyncLocal flow works fine for production but is harder to reason about in
    /// unit tests, where we want one DbContext = one identity.
    /// </summary>
    private sealed class TestAccountContext : IAccountContext
    {
        public Guid? UserId { get; init; }

        public Guid? AccountId { get; init; }

        public bool IsAdministrator { get; init; }
    }

    private static WriteDbContext CreateDbContext(string databaseName, IAccountContext accountContext)
    {
        // Matches the pattern the rest of this test suite uses (PermissionServiceTests,
        // JwtTokenServiceTests) — in-memory EF provider on a per-test database name so
        // each test owns its store and parallel-safe isolation works without any
        // cross-test contamination. The interceptor is added exactly as Program.cs's
        // AddDbContext would do via its factory overload, including the same scoped
        // lifetime reasoning.
        //
        // The shared InMemoryDatabaseRoot is critical here: each call to
        // DbContextOptionsBuilder builds its own internal service provider, and the
        // InMemory provider's store cache is keyed by (name, root). Without an
        // explicit root passed in, the seed-context and the read-context built with
        // matching names end up with two different stores — so the read sees zero
        // rows even though the seed clearly succeeded. The official Microsoft Learn
        // guidance for InMemory cross-context sharing is exactly this:
        // https://learn.microsoft.com/en-us/ef/core/testing/testing-without-the-database
        var interceptor = new RowLevelSecurityInterceptor(accountContext);
        var dbContextOptions = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(databaseName, SharedInMemoryDatabaseRoot)
            .AddInterceptors(interceptor)
            .Options;
        return new WriteDbContext(dbContextOptions, accountContext);
    }

    // One InMemoryDatabaseRoot per test class so every DbContext built by this suite
    // shares the same in-memory store for a given name. Test parallelism is still
    // safe because each test uses its own Guid database name.
    private static readonly InMemoryDatabaseRoot SharedInMemoryDatabaseRoot = new();
}