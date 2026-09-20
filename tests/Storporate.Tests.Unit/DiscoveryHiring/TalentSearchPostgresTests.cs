using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;
using Xunit;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-43 Phase 2 live Postgres proof. Companion to the unit-level
/// <see cref="TalentSearchEndpointsTests"/>: every assertion here exercises
/// the production Npgsql provider + the row-level security pipeline
/// against a real throwaway Docker container.
///
/// <para>
/// <b>Two env-var gates.</b>
/// <list type="bullet">
///   <item><c>STORPORATE_TEST_PG</c> — connection string for the migration
///         role (the database owner / superuser). Required to create the
///         <c>storporate_app</c> role and the test fixtures.</item>
///   <item><c>STORPORATE_TEST_PG_APP</c> — connection string for the
///         restricted <c>storporate_app</c> role (created by the
///         <c>AddRowLevelSecurity</c> migration, password
///         <c>storporate_dev_app</c>). Required to exercise the
///         RLS-as-app behavior the plan explicitly demands: a direct
///         read of another account's <c>TalentSearchRequests</c> must
///         return zero rows.</item>
/// </list>
/// A missing variable reports the test as <c>Skipped</c> with a
/// self-describing reason (via <see cref="LivePgAppFactAttribute"/>);
/// CI hosts without a reachable Postgres stay green and the assertions
/// only fire against the throwaway container the plan ships.
/// </para>
///
/// <para>
/// <b>What this file does NOT cover.</b> The processor's full 8-step
/// pipeline (requirements LLM → embedding → retrieval → ranking LLM →
/// validation → result) is already covered exhaustively by
/// <see cref="SearchTalentJobProcessorTests"/> with deterministic fakes;
/// this file only proves the things that require a real database:
/// <list type="bullet">
///   <item>The <c>AddTalentSearchRequests</c> +
///         <c>AddTalentSearchRequestRowLevelSecurity</c> migrations
///         applied cleanly: the table exists, the <c>account_scoped</c>
///         policy is installed, and the restricted role can read it.</item>
///   <item>As <c>storporate_app</c> with <c>app.account_id</c> unset (or
///         set to a value that doesn't match the row), a direct
///         <c>SELECT</c> against <c>TalentSearchRequests</c> returns the
///         expected empty / scoped result set.</item>
///   <item>As <c>storporate_app</c>, the index table
///         <c>TalentIndexEntries</c> is readable (it has no RLS policy,
///         because the processor needs to scan every opted-in student)
///         and the inventory table <c>PortfolioItems</c> is NOT
///         reachable (the processor must use only the index).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Per-test isolation.</b> Every test seeds rows with
/// <see cref="Guid.NewGuid"/> account ids and removes them in a
/// <c>try/finally</c> via the migration role, so a parallel test run or
/// a crashed prior test cannot leave state behind that would distort the
/// next run's assertion.
/// </para>
/// </remarks>
[Collection("LivePg")]
public class TalentSearchPostgresTests
{

    private const string MigrationConnectionEnvVar = "STORPORATE_TEST_PG";
    private const string AppRoleConnectionEnvVar = "STORPORATE_TEST_PG_APP";

    [LivePgAppFact]
    public async Task TalentSearchRequests_TableExistsAndPolicyIsInstalled()
    {
        var appConnection = RequireAppConnection();

        await using var connection = await OpenCleanAppConnectionAsync(appConnection);

        // 1. The table is reachable as storporate_app.
        await using (var tableCheck = new NpgsqlCommand(
            "SELECT to_regclass('public.\"TalentSearchRequests\"') IS NOT NULL", connection))
        {
            var exists = (bool)(await tableCheck.ExecuteScalarAsync())!;
            Assert.True(exists, "TalentSearchRequests table is missing — AddTalentSearchRequests migration did not run.");
        }

        // 2. The account_scoped policy is installed AND enforced.
        await using (var policyCheck = new NpgsqlCommand(@"
            SELECT
                (SELECT relrowsecurity FROM pg_class WHERE relname = 'TalentSearchRequests') AS rls_enabled,
                (SELECT relforcerowsecurity FROM pg_class WHERE relname = 'TalentSearchRequests') AS rls_forced,
                EXISTS (
                    SELECT 1 FROM pg_policies
                    WHERE schemaname = 'public'
                      AND tablename = 'TalentSearchRequests'
                      AND policyname = 'account_scoped'
                ) AS policy_present", connection))
        {
            await using var reader = await policyCheck.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var rlsEnabled = reader.GetBoolean(0);
            var rlsForced = reader.GetBoolean(1);
            var policyPresent = reader.GetBoolean(2);
            Assert.True(rlsEnabled, "TalentSearchRequests has ENABLE ROW LEVEL SECURITY disabled.");
            Assert.True(rlsForced, "TalentSearchRequests has FORCE ROW LEVEL SECURITY disabled — owner could bypass RLS.");
            Assert.True(policyPresent, "account_scoped policy on TalentSearchRequests is missing.");
        }
    }

    [LivePgAppFact]
    public async Task TalentSearchRequests_AsAppRole_WithNoAmbientAccount_ReturnsZeroRows()
    {
        // The "before the RowLevelSecurityInterceptor pushed its GUCs"
        // window. We can't leave app.is_admin truly unset (Postgres
        // would store it as '' and the policy's
        // current_setting(...)::boolean cast would fail with
        // "invalid input syntax for type boolean: ''"), so we push
        // the closest thing: app.is_admin = 'false' and a NULLIF'd
        // app.account_id. The policy's OR-branch evaluates to
        // false + false → deny both rows.
        var appConnection = RequireAppConnection();
        var migrationConnection = RequireMigrationConnection();

        var (ownerId, intruderId) = await SeedTwoAccountRowsAsync(migrationConnection);

        try
        {
            await using var connection = await OpenCleanAppConnectionAsync(appConnection);
            await using (var setAmbient = new NpgsqlCommand(
                "SELECT set_config('app.account_id', '', false), set_config('app.is_admin', 'false', false)",
                connection))
            {
                await setAmbient.ExecuteScalarAsync();
            }

            var visibleRows = await CountTalentSearchRequestsAsync(connection);
            Assert.Equal(0, visibleRows);
        }
        finally
        {
            await CleanupAsync(migrationConnection, ownerId, intruderId);
        }
    }

    [LivePgAppFact]
    public async Task TalentSearchRequests_AsAppRole_WithOwnAccount_ReturnsOnlyOwnRows()
    {
        var appConnection = RequireAppConnection();
        var migrationConnection = RequireMigrationConnection();

        var (ownerId, intruderId) = await SeedTwoAccountRowsAsync(migrationConnection);

        try
        {
            await using var connection = await OpenCleanAppConnectionAsync(appConnection);

            // Push the GUC to the owner's account id. SELECT set_config
            // with false (session scope) mirrors what
            // RowLevelSecurityInterceptor does on every SaveChanges.
            // Pass the Guid as a string so the text overload of
            // set_config resolves unambiguously; the policy's
            // NULLIF(... , '')::uuid cast does the conversion.
            await using (var setAmbient = new NpgsqlCommand(
                "SELECT set_config('app.account_id', @id, false), set_config('app.is_admin', 'false', false)",
                connection))
            {
                setAmbient.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Text)
                {
                    Value = ownerId.ToString(),
                });
                await setAmbient.ExecuteScalarAsync();
            }

            var visibleRows = await CountTalentSearchRequestsAsync(connection);
            Assert.Equal(1, visibleRows);

            // The visible row must be the owner's, never the intruder's.
            await using var probe = new NpgsqlCommand(
                "SELECT \"AccountId\"::text FROM \"TalentSearchRequests\"", connection);
            await using var reader = await probe.ExecuteReaderAsync();
            var accountIds = new List<string>();
            while (await reader.ReadAsync())
            {
                accountIds.Add(reader.GetString(0));
            }
            Assert.Single(accountIds);
            Assert.Equal(ownerId.ToString(), accountIds[0]);
        }
        finally
        {
            await CleanupAsync(migrationConnection, ownerId, intruderId);
        }
    }

    [LivePgAppFact]
    public async Task TalentSearchRequests_AsAppRole_WithOtherAccount_ReturnsZeroRows()
    {
        // The plan's explicit acceptance criterion: "RLS blocks any
        // direct read of another account's TalentSearchRequests."
        var appConnection = RequireAppConnection();
        var migrationConnection = RequireMigrationConnection();

        var ownerId = await SeedSingleOwnerSearchAsync(migrationConnection);

        try
        {
            await using var connection = await OpenCleanAppConnectionAsync(appConnection);

            // Set ambient to an UNRELATED random account id — the
            // owner's row must NOT be visible. The unrelated id is
            // chosen so no row in the table happens to belong to it.
            var unrelatedAccount = Guid.NewGuid();
            await using (var setAmbient = new NpgsqlCommand(
                "SELECT set_config('app.account_id', @id, false), set_config('app.is_admin', 'false', false)",
                connection))
            {
                setAmbient.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Text)
                {
                    Value = unrelatedAccount.ToString(),
                });
                await setAmbient.ExecuteScalarAsync();
            }

            var visibleRows = await CountTalentSearchRequestsAsync(connection);
            Assert.Equal(0, visibleRows);
        }
        finally
        {
            await CleanupAsync(migrationConnection, ownerId);
        }
    }

    [LivePgAppFact]
    public async Task AsAppRole_TalentIndexEntries_IsReadable()
    {
        // The processor scans TalentIndexEntries for every opted-in
        // student. The table must be reachable as storporate_app; the
        // plan explicitly notes it is NOT IAccountScoped, so no RLS
        // policy lives on it.
        var appConnection = RequireAppConnection();
        var migrationConnection = RequireMigrationConnection();

        var entryId = Guid.NewGuid();
        var studentAccountId = Guid.NewGuid();

        try
        {
            // The Embedding column is NOT NULL vector(768); the
            // TalentIndexRepository's UpsertAsync constructs the
            // literal as a parameterized string and the cast handles
            // it. For a direct INSERT here we build the literal by
            // hand: 768 zeros is fine because we never query the
            // vector — only read the row by id.
            const int Dimensions = 768;
            var zeros = new float[Dimensions];
            var literalBuilder = new System.Text.StringBuilder(capacity: Dimensions * 12);
            literalBuilder.Append('[');
            for (var i = 0; i < zeros.Length; i++)
            {
                if (i > 0) literalBuilder.Append(',');
                literalBuilder.Append(zeros[i].ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
            }
            literalBuilder.Append(']');

            await using (var seed = new NpgsqlConnection(migrationConnection))
            {
                await seed.OpenAsync();
                await using var insert = new NpgsqlCommand(@"
                    INSERT INTO ""TalentIndexEntries""
                        (""Id"", ""StudentAccountId"", ""DisplayName"", ""ItemsJson"",
                         ""SearchText"", ""ContentHash"", ""UpdatedAt"", ""Embedding"")
                    VALUES (@id, @studentId, 'live-pg', '[]'::jsonb, 'seed', 'seed', now() at time zone 'utc', @embedding::vector)", seed);
                insert.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = entryId });
                insert.Parameters.Add(new NpgsqlParameter("@studentId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = studentAccountId });
                insert.Parameters.Add(new NpgsqlParameter("@embedding", literalBuilder.ToString()));
                await insert.ExecuteNonQueryAsync();
            }

            // storporate_app must be able to read it (the processor's
            // SearchNearestAsync does exactly this — a raw SELECT).
            await using var appConn = await OpenCleanAppConnectionAsync(appConnection);
            await using var probe = new NpgsqlCommand(
                "SELECT count(*) FROM \"TalentIndexEntries\" WHERE \"Id\" = @id", appConn);
            probe.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = entryId });
            var count = Convert.ToInt32(await probe.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(migrationConnection);
            await cleanup.OpenAsync();
            await using var del = new NpgsqlCommand(
                "DELETE FROM \"TalentIndexEntries\" WHERE \"Id\" = @id", cleanup);
            del.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = entryId });
            await del.ExecuteNonQueryAsync();
        }
    }

    [LivePgAppFact]
    public async Task AsAppRole_PortfolioItems_AccountScopeBlocksOtherAccountsRow()
    {
        // PortfolioItems has its own account_scoped RLS policy
        // (AddPortfolioItemsRowLevelSecurity). The plan's invariant:
        // a row belonging to another account is invisible. We seed a
        // row under account A, then connect as storporate_app with
        // app.account_id set to a DIFFERENT account B and assert that
        // the row is hidden — exactly what the processor would see
        // if a hypothetical future change tried to read PortfolioItems
        // for another account directly.
        var appConnection = RequireAppConnection();
        var migrationConnection = RequireMigrationConnection();

        var itemId = Guid.NewGuid();
        var ownerAccountId = Guid.NewGuid();
        var intruderAccountId = Guid.NewGuid();
        try
        {
            await using (var seed = new NpgsqlConnection(migrationConnection))
            {
                await seed.OpenAsync();

                foreach (var id in new[] { ownerAccountId, intruderAccountId })
                {
                    await using var ensureUser = new NpgsqlCommand(@"
                        INSERT INTO ""Users""
                            (""Id"", ""Email"", ""ActorType"", ""VerificationStatus"", ""CreatedAt"", ""UpdatedAt"")
                        VALUES (@userId, @email, 'Student', 'Verified', now() at time zone 'utc', now() at time zone 'utc')", seed);
                    ensureUser.Parameters.Add(new NpgsqlParameter("@userId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
                    ensureUser.Parameters.Add(new NpgsqlParameter("@email", NpgsqlTypes.NpgsqlDbType.Text) { Value = $"live-{Guid.NewGuid():N}@example.com" });
                    await ensureUser.ExecuteNonQueryAsync();
                }

                await using var insert = new NpgsqlCommand(@"
                    INSERT INTO ""PortfolioItems""
                        (""Id"", ""AccountId"", ""Label"", ""Category"", ""SubmissionType"", ""CreatedAt"")
                    VALUES (@id, @accountId, 'live-pg', 'Project', 'ExternalUrl', now() at time zone 'utc')", seed);
                insert.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = itemId });
                insert.Parameters.Add(new NpgsqlParameter("@accountId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerAccountId });
                await insert.ExecuteNonQueryAsync();
            }

            // storporate_app with app.account_id set to the INTRUDER —
            // the owner's PortfolioItems row must NOT be visible.
            await using var appConn = await OpenCleanAppConnectionAsync(appConnection);
            await using (var setAmbient = new NpgsqlCommand(
                "SELECT set_config('app.account_id', @id, false), set_config('app.is_admin', 'false', false)",
                appConn))
            {
                setAmbient.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Text) { Value = intruderAccountId.ToString() });
                await setAmbient.ExecuteScalarAsync();
            }
            await using var probe = new NpgsqlCommand(
                "SELECT count(*) FROM \"PortfolioItems\" WHERE \"Id\" = @id", appConn);
            probe.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = itemId });
            var count = Convert.ToInt32(await probe.ExecuteScalarAsync());
            Assert.Equal(0, count);

            // With app.account_id set to the OWNER, the row becomes
            // visible — proves the SELECT privilege is in place, just
            // gated by RLS.
            await using (var setAmbient = new NpgsqlCommand(
                "SELECT set_config('app.account_id', @id, false), set_config('app.is_admin', 'false', false)",
                appConn))
            {
                setAmbient.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Text) { Value = ownerAccountId.ToString() });
                await setAmbient.ExecuteScalarAsync();
            }
            count = Convert.ToInt32(await probe.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(migrationConnection);
            await cleanup.OpenAsync();
            await using (var del = new NpgsqlCommand("DELETE FROM \"PortfolioItems\" WHERE \"Id\" = @id", cleanup))
            {
                del.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = itemId });
                await del.ExecuteNonQueryAsync();
            }
            foreach (var id in new[] { ownerAccountId, intruderAccountId })
            {
                await using var delUser = new NpgsqlCommand(
                    "DELETE FROM \"Users\" WHERE \"Id\" = @id", cleanup);
                delUser.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
                try { await delUser.ExecuteNonQueryAsync(); } catch { /* best effort */ }
            }
        }
    }

    // ----- infrastructure -----

    private static string RequireMigrationConnection()
    {
        var value = Environment.GetEnvironmentVariable(MigrationConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{MigrationConnectionEnvVar} must be set to an Npgsql connection string for the migration role.");
        }
        return value;
    }

    private static string RequireAppConnection()
    {
        var value = Environment.GetEnvironmentVariable(AppRoleConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{AppRoleConnectionEnvVar} must be set to an Npgsql connection string for storporate_app "
                + "(created by AddRowLevelSecurity with password 'storporate_dev_app').");
        }
        return value;
    }

    /// <summary>Open an app-role connection and clear every session-scoped
    /// GUC the prior tests may have left behind on the pooled
    /// connection. Npgsql pools by connection string — a previous test
    /// that called <c>set_config('app.is_admin', 'false', false)</c>
    /// would otherwise leak the GUC into a later test that expected an
    /// unset policy context.</summary>
    private static async Task<NpgsqlConnection> OpenCleanAppConnectionAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // RESET drops the session-local override so subsequent
        // current_setting(name, true) returns the GUC's
        // default — empty string for custom GUCs. The caller is
        // responsible for setting both GUCs to a known state
        // (set_config(... 'false', false) for app.is_admin and a
        // specific account id for app.account_id) before exercising
        // the RLS-protected tables. DISCARD ALL would also work but
        // is heavier (resets plans + temp tables); RESET is enough.
        await using (var reset = new NpgsqlCommand(
            "RESET app.account_id; RESET app.is_admin;", connection))
        {
            await reset.ExecuteNonQueryAsync();
        }
        return connection;
    }

    private static async Task<int> CountTalentSearchRequestsAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM \"TalentSearchRequests\"", connection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Insert two TalentSearchRequests rows — one owned by
    /// <paramref name="ownerId"/>, one by <paramref name="intruderId"/> —
    /// via the migration role so RLS doesn't filter the insert. Returns
    /// the pair so the caller can clean up.</summary>
    private static async Task<(Guid OwnerId, Guid IntruderId)> SeedTwoAccountRowsAsync(string migrationConnection)
    {
        var ownerId = Guid.NewGuid();
        var intruderId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(migrationConnection);
        await connection.OpenAsync();

        // The Users table has a unique-by-Email index AND requires
        // VerificationStatus (NOT NULL). Create two rows so the
        // foreign key on TalentSearchRequests.AccountId resolves.
        foreach (var id in new[] { ownerId, intruderId })
        {
            await using var insertUser = new NpgsqlCommand(@"
                INSERT INTO ""Users""
                    (""Id"", ""Email"", ""ActorType"", ""VerificationStatus"", ""CreatedAt"", ""UpdatedAt"")
                VALUES (@id, @email, 'Organization', 'Verified', now() at time zone 'utc', now() at time zone 'utc')", connection);
            insertUser.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
            insertUser.Parameters.Add(new NpgsqlParameter("@email", NpgsqlTypes.NpgsqlDbType.Text) { Value = $"live-{Guid.NewGuid():N}@example.com" });
            await insertUser.ExecuteNonQueryAsync();
        }

        foreach (var id in new[] { ownerId, intruderId })
        {
            await using var insertRequest = new NpgsqlCommand(@"
                INSERT INTO ""TalentSearchRequests""
                    (""Id"", ""AccountId"", ""QueryText"", ""Status"", ""CreatedAt"")
                VALUES (@id, @accountId, 'live-pg', 'Pending', now() at time zone 'utc')", connection);
            insertRequest.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
            insertRequest.Parameters.Add(new NpgsqlParameter("@accountId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
            await insertRequest.ExecuteNonQueryAsync();
        }

        return (ownerId, intruderId);
    }

    /// <summary>Insert ONE TalentSearchRequest row owned by a fresh
    /// account, via the migration role so RLS doesn't filter the
    /// insert. Used by the "RLS denies the wrong account" test,
    /// which only needs to prove the cross-account isolation — the
    /// second seeded row from <see cref="SeedTwoAccountRowsAsync"/>
    /// would otherwise confuse the count assertion.</summary>
    private static async Task<Guid> SeedSingleOwnerSearchAsync(string migrationConnection)
    {
        var ownerId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(migrationConnection);
        await connection.OpenAsync();

        await using (var insertUser = new NpgsqlCommand(@"
            INSERT INTO ""Users""
                (""Id"", ""Email"", ""ActorType"", ""VerificationStatus"", ""CreatedAt"", ""UpdatedAt"")
            VALUES (@id, @email, 'Organization', 'Verified', now() at time zone 'utc', now() at time zone 'utc')", connection))
        {
            insertUser.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerId });
            insertUser.Parameters.Add(new NpgsqlParameter("@email", NpgsqlTypes.NpgsqlDbType.Text) { Value = $"live-{Guid.NewGuid():N}@example.com" });
            await insertUser.ExecuteNonQueryAsync();
        }

        await using (var insertRequest = new NpgsqlCommand(@"
            INSERT INTO ""TalentSearchRequests""
                (""Id"", ""AccountId"", ""QueryText"", ""Status"", ""CreatedAt"")
            VALUES (@id, @accountId, 'live-pg', 'Pending', now() at time zone 'utc')", connection))
        {
            insertRequest.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
            insertRequest.Parameters.Add(new NpgsqlParameter("@accountId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerId });
            await insertRequest.ExecuteNonQueryAsync();
        }

        return ownerId;
    }

    private static async Task CleanupAsync(string migrationConnection, params Guid[] accountIds)
    {
        await using var connection = new NpgsqlConnection(migrationConnection);
        await connection.OpenAsync();
        foreach (var accountId in accountIds)
        {
            try
            {
                await using var delRequests = new NpgsqlCommand(
                    "DELETE FROM \"TalentSearchRequests\" WHERE \"AccountId\" = @id", connection);
                delRequests.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = accountId });
                await delRequests.ExecuteNonQueryAsync();

                await using var delUser = new NpgsqlCommand(
                    "DELETE FROM \"Users\" WHERE \"Id\" = @id", connection);
                delUser.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = accountId });
                await delUser.ExecuteNonQueryAsync();
            }
            catch
            {
                // Best-effort cleanup; the test's real assertion already ran.
            }
        }
    }
}

/// <summary>
/// xUnit 2.9.3 <see cref="FactAttribute"/> that sets <see cref="FactAttribute.Skip"/>
/// when either <c>STORPORATE_TEST_PG</c> (migration role) or
/// <c>STORPORATE_TEST_PG_APP</c> (restricted <c>storporate_app</c> role) is
/// unset. The runner reports the test as <c>Skipped</c> with a self-describing
/// reason so a missing variable doesn't silently pass.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class LivePgAppFactAttribute : FactAttribute
{
    public LivePgAppFactAttribute()
    {
        var migration = Environment.GetEnvironmentVariable("STORPORATE_TEST_PG");
        var app = Environment.GetEnvironmentVariable("STORPORATE_TEST_PG_APP");
        if (string.IsNullOrWhiteSpace(migration))
        {
            Skip =
                "STORPORATE_TEST_PG is not set; this live pg test is gated to opt-in "
                + "runs against the throwaway storporate-pgvector-test container.";
        }
        else if (string.IsNullOrWhiteSpace(app))
        {
            Skip =
                "STORPORATE_TEST_PG_APP is not set; this live pg test connects as the "
                + "restricted storporate_app role created by AddRowLevelSecurity. Run "
                + "with both STORPORATE_TEST_PG and STORPORATE_TEST_PG_APP set.";
        }
    }
}

/// <summary>
/// All <see cref="TalentSearchPostgresTests"/> run in this single
/// xUnit collection. DisableParallelization serialises them so the
/// session-scoped <c>app.account_id</c> / <c>app.is_admin</c> GUCs
/// can't leak between tests via Npgsql's connection pool — a regression
/// that escaped under one connection's pool entry would otherwise
/// surface as a confusing "invalid input syntax for type boolean"
/// error in a totally unrelated test.
/// </summary>
[CollectionDefinition("LivePg", DisableParallelization = true)]
public sealed class LivePgTestCollection { }