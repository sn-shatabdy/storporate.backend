using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations;

/// <summary>
/// STOR-43 Cross-Validation follow-up: harden the <c>account_scoped</c>
/// PostgreSQL row-level security policies installed by this story against an
/// empty-string <c>app.is_admin</c> / <c>app.account_id</c> GUC.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this fixes.</b> The original policies (see
/// <c>AddStudentSearchProfileRowLevelSecurity</c> and
/// <c>AddTalentSearchRequestRowLevelSecurity</c>) cast
/// <c>current_setting('app.is_admin', true)</c> to <c>boolean</c>. When the
/// GUC has not been set at all, <c>current_setting(..., true)</c> returns
/// the empty string — and <c>::boolean</c> on <c>''</c> throws
/// <c>22P02 invalid input syntax for type boolean</c>. The production
/// <c>RowLevelSecurityInterceptor</c> always sets the GUC before any EF
/// query, so the bug only surfaces for raw-SQL bypass paths (background
/// workers that open their own connections without the interceptor, psql
/// sessions for ops/debugging, and ad-hoc ETL). This migration replaces
/// the policies installed by THIS ticket's RLS migrations with a version
/// that wraps each <c>current_setting(...)</c> read in <c>NULLIF(..., '')</c>
/// so an empty GUC short-circuits to a <c>NULL</c> that casts cleanly to
/// <c>boolean</c> / <c>uuid</c> rather than throwing.
/// </para>
/// <para>
/// <b>Scope — only this ticket's tables.</b> The plan is explicit: only
/// harden <c>StudentSearchProfiles</c> and <c>TalentSearchRequests</c>.
/// <c>TalentIndexEntries</c> is non-tenant by design and has no policy to
/// harden. Older migrations are NOT edited per the plan's "Do not edit
/// older migrations" rule — they keep their original shapes; if a future
/// ticket wants to harden the rest, it can copy this pattern.
/// </para>
/// <para>
/// <b>Down semantics.</b> Restore the original (pre-hardening) policy
/// bodies verbatim, so a <c>database update PreviousMigration</c> lands
/// on exactly the SQL the original migrations would have installed.
/// </para>
/// </remarks>
public partial class HardenTalentSearchRlsEmptyGuc : Migration
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>Tables whose policies this migration rewrites.
    /// Ordered alphabetically for deterministic Down output.</summary>
    private static readonly string[] HardenedTables = { "StudentSearchProfiles", "TalentSearchRequests" };

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
        {
            return;
        }

        foreach (var table in HardenedTables)
        {
            migrationBuilder.Sql(
                $"""
                ALTER TABLE "{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "{table}" FORCE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS account_scoped ON "{table}";

                -- Hardened predicate: NULLIF(..., '') collapses an unset/empty GUC to NULL,
                -- and COALESCE(NULL, FALSE) keeps the deny-by-default behaviour while making
                -- the policy immune to an empty-string GUC raised by raw-SQL paths.
                CREATE POLICY account_scoped ON "{table}"
                    USING (
                        COALESCE(NULLIF(current_setting('app.is_admin', true), '')::boolean, FALSE)
                        OR "AccountId" = COALESCE(NULLIF(current_setting('app.account_id', true), '')::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
                    )
                    WITH CHECK (
                        COALESCE(NULLIF(current_setting('app.is_admin', true), '')::boolean, FALSE)
                        OR "AccountId" = COALESCE(NULLIF(current_setting('app.account_id', true), '')::uuid, '00000000-0000-0000-0000-000000000000'::uuid)
                    );
                """);
        }
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
        {
            return;
        }

        // Restore the original (pre-hardening) policy bodies verbatim from
        // AddStudentSearchProfileRowLevelSecurity / AddTalentSearchRequestRowLevelSecurity.
        // The FORCE / DISABLE flags are cleared after the policy drop so a
        // `database update PreviousMigration` lands on the same
        // table-level state the prior migrations would have left — matching
        // the prior art's Down sequence (policy drop → NO FORCE → DISABLE).
        foreach (var table in HardenedTables)
        {
            migrationBuilder.Sql(
                $"""
                DROP POLICY IF EXISTS account_scoped ON "{table}";

                CREATE POLICY account_scoped ON "{table}"
                    USING (
                        current_setting('app.is_admin', true)::boolean
                        OR "AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                    )
                    WITH CHECK (
                        current_setting('app.is_admin', true)::boolean
                        OR "AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                    );

                ALTER TABLE "{table}" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE "{table}" DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
