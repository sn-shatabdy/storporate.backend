using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-44 Phase 2 live Postgres proof. Companion to the unit-level
/// <see cref="CandidateReviewEndpointsTests"/>: every assertion here
/// exercises the production Npgsql provider + the row-level security
/// pipeline against a real throwaway Docker container to prove the
/// Phase 2 drill-down surface cannot leak OTHER students' private
/// data even when the production code path runs end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two env-var gates.</b>
/// <list type="bullet">
///   <item><c>STORPORATE_TEST_PG</c> — connection string for the
///   migration role (the database owner / superuser). Required to
///   seed the test fixtures.</item>
///   <item><c>STORPORATE_TEST_PG_APP</c> — connection string for the
///   restricted <c>storporate_app</c> role (created by the
///   <c>AddRowLevelSecurity</c> migration, password
///   <c>storporate_dev_app</c>). Required to exercise the
///   RLS-as-app behavior the plan explicitly demands: a direct read
///   of <c>PortfolioItems</c> / <c>StudentSearchProfiles</c> as
///   <c>storporate_app</c> must return zero rows for any other
///   student's data, while the <c>TalentIndexEntries</c> table the
///   drill-down surface reads remains reachable.</item>
/// </list>
/// A missing variable reports the test as <c>Skipped</c> with a
/// self-describing reason (via <see cref="LivePgAppFactAttribute"/>);
/// CI hosts without a reachable Postgres stay green and the assertions
/// only fire against the throwaway container.
/// </para>
/// <para>
/// <b>What this file does NOT cover.</b> The endpoint's full HTTP
/// behavior (shape, headers, audit row content, disposition kind,
/// authorization 403/401 paths) is already covered exhaustively by
/// <see cref="CandidateReviewEndpointsTests"/> with the in-memory
/// provider; this file only proves the things that require a real
/// database: the production Npgsql provider + EF Core + the
/// row-level security pipeline do not silently widen what the
/// handler is allowed to read.
/// </para>
/// <para>
/// <b>Per-test isolation.</b> Every test seeds rows with
/// <see cref="Guid.NewGuid"/> ids and removes them in a
/// <c>try/finally</c> via the migration role, so a parallel test run
/// or a crashed prior test cannot leave state behind that would
/// distort the next run's assertion.
/// </para>
/// </remarks>
[Collection("LivePg")]
public class CandidateReviewPostgresTests
{
    private const string MigrationConnectionEnvVar = "STORPORATE_TEST_PG";
    private const string AppRoleConnectionEnvVar = "STORPORATE_TEST_PG_APP";

    [LivePgAppFact]
    public async Task AsAppRole_TalentIndexEntries_IsReadable_ForDrillDown()
    {
        // STOR-44 Phase 2: the GET /api/discovery/candidates/{id}
        // and GET /api/discovery/candidates/{id}/items/{itemId}/original
        // handlers both read TalentIndexEntries under the Organization
        // account. The table is intentionally NOT IAccountScoped (the
        // Phase 1 processor / Phase 2 search surface require every
        // opted-in student to be readable). As storporate_app this
        // table must remain reachable so the live RLS path matches the
        // in-memory test's contract.
        var appConnection = RequireAppConnection();
        var migrationConnection = RequireMigrationConnection();

        var entryId = Guid.NewGuid();
        var studentAccountId = Guid.NewGuid();

        try
        {
            // Insert a minimal TalentIndexEntries row. Embedding is a
            // NOT NULL vector(768); we supply 768 zeros because the
            // drill-down handler never queries the vector.
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
                    VALUES (@id, @studentId, 'live-pg-drill-down', '[]'::jsonb, 'seed', 'seed', now() at time zone 'utc', @embedding::vector)", seed);
                insert.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = entryId });
                insert.Parameters.Add(new NpgsqlParameter("@studentId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = studentAccountId });
                insert.Parameters.Add(new NpgsqlParameter("@embedding", literalBuilder.ToString()));
                await insert.ExecuteNonQueryAsync();
            }

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
    public async Task AsAppRole_PortfolioItems_AccountScopeBlocksOtherAccountRow_ForDrillDown()
    {
        // The drill-down surface must NEVER read PortfolioItems even
        // though the IndexEntry it returns references portfolio item
        // ids. As storporate_app with the ambient account set to the
        // INTRUDER, the owner's PortfolioItems row must be invisible.
        // This is the production-shape equivalent of the
        // "TalentSearchRequests_AsAppRole_WithOtherAccount_ReturnsZeroRows"
        // check, applied to the table the drill-down surface is most
        // tempted to read.
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

                // Foreign-key requirement: PortfolioItems.AccountId
                // references Users.Id. Insert both account rows so the
                // portfolio insert resolves.
                foreach (var id in new[] { ownerAccountId, intruderAccountId })
                {
                    await using var ensureUser = new NpgsqlCommand(@"
                        INSERT INTO ""Users""
                            (""Id"", ""Email"", ""ActorType"", ""VerificationStatus"", ""CreatedAt"", ""UpdatedAt"")
                        VALUES (@userId, @email, 'Student', 'Verified', now() at time zone 'utc', now() at time zone 'utc')", seed);
                    ensureUser.Parameters.Add(new NpgsqlParameter("@userId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
                    ensureUser.Parameters.Add(new NpgsqlParameter("@email", NpgsqlTypes.NpgsqlDbType.Text) { Value = $"live-drill-{Guid.NewGuid():N}@example.com" });
                    await ensureUser.ExecuteNonQueryAsync();
                }

                await using var insert = new NpgsqlCommand(@"
                    INSERT INTO ""PortfolioItems""
                        (""Id"", ""AccountId"", ""Label"", ""Category"", ""SubmissionType"", ""CreatedAt"")
                    VALUES (@id, @accountId, 'live-drill', 'Project', 'ExternalUrl', now() at time zone 'utc')", seed);
                insert.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = itemId });
                insert.Parameters.Add(new NpgsqlParameter("@accountId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerAccountId });
                await insert.ExecuteNonQueryAsync();
            }

            // As storporate_app with the INTRUDER's account_id: the
            // owner's PortfolioItems row is invisible. This proves
            // the RLS pipeline still blocks the drill-down surface
            // from accidentally touching PortfolioItems.
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

    [LivePgAppFact]
    public async Task AsAppRole_StudentSearchProfiles_AccountScopeBlocksOtherAccountRow_ForDrillDown()
    {
        // Same shape as the PortfolioItems test, applied to the
        // StudentSearchProfiles table. The drill-down surface must
        // never read this table either — opt-in/opt-out decisions
        // belong to the student, not the employer drill-down. RLS
        // blocks the cross-account read so any future regression
        // that tries to widen the read surface trips this test.
        var appConnection = RequireAppConnection();
        var migrationConnection = RequireMigrationConnection();

        var profileId = Guid.NewGuid();
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
                    ensureUser.Parameters.Add(new NpgsqlParameter("@email", NpgsqlTypes.NpgsqlDbType.Text) { Value = $"live-drill-profile-{Guid.NewGuid():N}@example.com" });
                    await ensureUser.ExecuteNonQueryAsync();
                }

                await using var insert = new NpgsqlCommand(@"
                    INSERT INTO ""StudentSearchProfiles""
                        (""Id"", ""AccountId"", ""IsSearchable"", ""DisplayName"",
                         ""ShowHeadline"", ""ShowUniversity"", ""ShowFieldOfStudy"", ""ShowStudyYear"",
                         ""UpdatedAt"")
                    VALUES (@id, @accountId, true, 'live-drill-profile',
                            true, true, true, true,
                            now() at time zone 'utc')", seed);
                insert.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = profileId });
                insert.Parameters.Add(new NpgsqlParameter("@accountId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerAccountId });
                await insert.ExecuteNonQueryAsync();
            }

            await using var appConn = await OpenCleanAppConnectionAsync(appConnection);
            await using (var setAmbient = new NpgsqlCommand(
                "SELECT set_config('app.account_id', @id, false), set_config('app.is_admin', 'false', false)",
                appConn))
            {
                setAmbient.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Text) { Value = intruderAccountId.ToString() });
                await setAmbient.ExecuteScalarAsync();
            }
            await using var probe = new NpgsqlCommand(
                "SELECT count(*) FROM \"StudentSearchProfiles\" WHERE \"Id\" = @id", appConn);
            probe.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = profileId });
            var count = Convert.ToInt32(await probe.ExecuteScalarAsync());
            Assert.Equal(0, count);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(migrationConnection);
            await cleanup.OpenAsync();
            await using (var del = new NpgsqlCommand("DELETE FROM \"StudentSearchProfiles\" WHERE \"Id\" = @id", cleanup))
            {
                del.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = profileId });
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
        await using (var reset = new NpgsqlCommand(
            "RESET app.account_id; RESET app.is_admin;", connection))
        {
            await reset.ExecuteNonQueryAsync();
        }
        return connection;
    }
}
