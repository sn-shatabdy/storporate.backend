using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
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

    [Fact]
    public async Task ReadQuery_AmbientContextChangeBetweenTwoReads_ReflectsLatestAccount()
    {
        // Phase 5 RLS fix — "stale pooled connection" scenario the IDbCommandInterceptor
        // hooks close: two Job rows under different AccountIds, ambient context flips
        // from A to B with no SaveChanges in between, and the second read must resolve
        // through the fresh account. In production this is exactly what happens when
        // Npgsql hands the same pooled connection to a different request — the global
        // query filter and the session-GUC RLS policy both have to be regenerated for
        // the new request. Under InMemory the GUC SQL inside ApplyPostgresGucs
        // short-circuits on the connection-type check, but the read path itself stays
        // correct because the global query filter evaluates the ambient context fresh
        // on every query — and the new IDbCommandInterceptor hooks exist to ensure the
        // same is true on a real Npgsql connection where the InMemory provider's
        // short-circuit doesn't apply.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();
        await SeedJobAsync(databaseName, accountA, "A's job");
        await SeedJobAsync(databaseName, accountB, "B's job");

        var mutableContext = new MutableTestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
        };

        await using var dbContext = CreateDbContext(databaseName, mutableContext);

        // First read — ambient context is A. The global query filter narrows to A's row.
        var fromAccountA = await dbContext.Jobs.ToListAsync();

        Assert.Single(fromAccountA);
        Assert.Equal(accountA, fromAccountA[0].AccountId);

        // Second read — ambient context flips to B with no SaveChanges in between. In
        // production this is exactly the "stale pooled connection" scenario the new
        // IDbCommandInterceptor hooks exist to defend against; here it proves the read
        // path stays correct when the connection state would otherwise differ.
        mutableContext.SetAccountId(accountB);
        mutableContext.SetUserId(accountB);

        var fromAccountB = await dbContext.Jobs.ToListAsync();

        Assert.Single(fromAccountB);
        Assert.Equal(accountB, fromAccountB[0].AccountId);
    }

    [Fact]
    public void CommandInterceptorHooks_ReReadAmbientContextOnEveryInvocation()
    {
        // Phase 5 RLS fix — direct mechanism test. Drives the production interceptor's
        // GUC SQL builder through the internal BuildGucSetCommandForTest seam (see
        // Storporate.Infrastructure/Properties/AssemblyInfo.cs for the InternalsVisibleTo
        // grant) to verify that the SQL the interceptor would emit reflects the *live*
        // IAccountContext at the moment of the call — not a snapshot captured at
        // interceptor construction. This is the property the IDbCommandInterceptor hooks
        // protect for pooled Npgsql connections: even if a previous request set the GUCs
        // to account A's values, a subsequent request that reads them through this
        // builder sees account B's values because the builder re-reads _accountContext
        // on every invocation.
        //
        // The GUC command is built (not executed) so we never touch a real Postgres
        // connection. Building is enough — it forces the IAccountContext read path and
        // the parameter values that would have gone out over the wire.
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var mutableContext = new MutableTestAccountContext
        {
            UserId = accountA,
            AccountId = accountA,
            IsAdministrator = false,
        };
        var interceptor = new RowLevelSecurityInterceptor(mutableContext);
        var connection = new NpgsqlConnection("Host=localhost;Database=unused;Username=unused;Password=unused");

        // First build — ambient context is account A. Inspect the parameters that would
        // have been sent over the wire.
        using (var commandA = interceptor.BuildGucSetCommandForTest(connection))
        {
            var accountParamA = (string?)commandA.Parameters["@accountId"].Value;
            var userParamA = (string?)commandA.Parameters["@userId"].Value;
            var isAdminParamA = (string?)commandA.Parameters["@isAdmin"].Value;
            Assert.Equal(accountA.ToString(), accountParamA);
            Assert.Equal(accountA.ToString(), userParamA);
            Assert.Equal("false", isAdminParamA);
        }

        // Mutate the ambient context — the production interceptor is the same instance
        // (no re-construction); if it captured values at construction this build would
        // still emit account A's parameters. The fact that it emits account B's proves
        // the GUC SQL uses the *current* IAccountContext.
        mutableContext.SetAccountId(accountB);
        mutableContext.SetUserId(accountB);

        using (var commandB = interceptor.BuildGucSetCommandForTest(connection))
        {
            var accountParamB = (string?)commandB.Parameters["@accountId"].Value;
            var userParamB = (string?)commandB.Parameters["@userId"].Value;
            var isAdminParamB = (string?)commandB.Parameters["@isAdmin"].Value;
            Assert.Equal(accountB.ToString(), accountParamB);
            Assert.Equal(accountB.ToString(), userParamB);
            Assert.Equal("false", isAdminParamB);

            // Defensive assertion: account A's values must NOT have leaked through into
            // the second build. A regression that cached values at construction would
            // fail this assertion with account A's guid.
            Assert.NotEqual(accountA.ToString(), accountParamB);
            Assert.NotEqual(accountA.ToString(), userParamB);
        }
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

    /// <summary>
    /// Mutable counterpart to <see cref="TestAccountContext"/> for tests that need to
    /// simulate the ambient context changing between two EF Core operations on the same
    /// DbContext (mirroring what would happen if a pooled connection handed back to the
    /// pool was reused by a different request). The read/write pair lets the
    /// direct-mechanism test assert that the production interceptor's GUC builder
    /// re-reads the live ambient context on every invocation — the property that closes
    /// the stale-GUC window for pooled Npgsql connections.
    /// </summary>
    private sealed class MutableTestAccountContext : IAccountContext
    {
        private Guid? _userId;
        private Guid? _accountId;
        private bool _isAdministrator;

        public Guid? UserId
        {
            get => _userId;
            init => _userId = value;
        }

        public Guid? AccountId
        {
            get => _accountId;
            init => _accountId = value;
        }

        public bool IsAdministrator
        {
            get => _isAdministrator;
            init => _isAdministrator = value;
        }

        public void SetUserId(Guid? value)
        {
            _userId = value;
        }

        public void SetAccountId(Guid? value)
        {
            _accountId = value;
        }

        public void SetIsAdministrator(bool value)
        {
            _isAdministrator = value;
        }
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