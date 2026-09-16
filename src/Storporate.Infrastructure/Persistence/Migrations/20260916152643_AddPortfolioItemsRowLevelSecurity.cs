using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations;

/// <summary>
/// STOR-37 Phase 1 follow-up: install the same <c>account_scoped</c> PostgreSQL row-level
/// security policy on <c>PortfolioItems</c> that <see cref="AddRowLevelSecurity"/> installed on
/// <c>Jobs</c>. The two migrations are intentionally split — the original STOR-62 migration
/// is already-applied history on <c>main</c> and is immutable, so this follow-up reuses the
/// exact same per-table RLS pattern for the one new <see cref="SharedKernel.Entities.IAccountScoped"/>
/// table introduced by this story.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate migration, not a row in <see cref="AddRowLevelSecurity"/>.</b>
/// <see cref="AddRowLevelSecurity"/> shipped on <c>main</c> in STOR-62 Phase 5 and is
/// already-applied history there. EF migrations are append-only — editing an applied
/// migration produces a new model snapshot that disagrees with the recorded history. Adding
/// this as a follow-up migration is the documented way to layer in a new
/// <see cref="SharedKernel.Entities.IAccountScoped"/> table's RLS coverage without rewriting
/// the past (see <see cref="AddRowLevelSecurity"/>'s own doc comment: "the RLS pipeline is
/// opt-in by design, not auto-applied — adding a new IAccountScoped entity must add a row
/// here (and a corresponding CREATE POLICY/ALTER TABLE block in Up/Down)" — except that the
/// 'row here' was <i>already past</i> by the time PortfolioItem landed).
/// </para>
/// <para>
/// <b>Idempotency, in common with <see cref="AddRowLevelSecurity"/>.</b> <c>DROP POLICY IF
/// EXISTS</c> on the way up + <c>CREATE POLICY</c> always; <c>DROP POLICY IF EXISTS</c> on
/// the way down. <c>ALTER TABLE ... ENABLE/FORCE</c> and the corresponding
/// <c>DISABLE/NO FORCE</c> are not conditionally guarded — Postgres treats repeated
/// <c>ENABLE</c> as a no-op and <c>DISABLE</c> on an already-disabled table likewise, so
/// running the migration twice (or running it after <see cref="AddRowLevelSecurity"/> has
/// already touched this table in a future restoration scenario) is safe.
/// </para>
/// <para>
/// <b>Provider gating.</b> Mirrors <see cref="AddRowLevelSecurity"/>: skip entirely under
/// any non-Npgsql provider so the InMemory test suite no-ops cleanly. The provider-name
/// check happens <i>before</i> any SQL is emitted.
/// </para>
/// </remarks>
public partial class AddPortfolioItemsRowLevelSecurity : Migration
{
    /// <summary>
    /// Exact provider name <see cref="Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder,string,System.Action{Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilder})"/>
    /// registers. Compared verbatim against <see cref="Migrations.MigrationBuilder.ActiveProvider"/>.
    /// </summary>
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// The one <see cref="SharedKernel.Entities.IAccountScoped"/> table this follow-up installs
    /// RLS coverage on. Kept as a constant (rather than walked from the model) for the same
    /// reason <see cref="AddRowLevelSecurity.AccountScopedTables"/> keeps its array
    /// explicit: the RLS policy is per-table DDL that has to be authored as a known set, and
    /// the migration must be source-controllable without runtime introspection.
    /// </summary>
    private const string TargetTable = "PortfolioItems";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Provider gate. The InMemory provider used by the test suite has
        // ActiveProvider == "Microsoft.EntityFrameworkCore.InMemory", so the SQL below is
        // skipped in unit tests. Mirrors AddRowLevelSecurity's provider gate so this
        // migration and its parent stay symmetric.
        if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
        {
            return;
        }

        // ENABLE + FORCE so the policy applies to the table owner too — important because
        // the connection role used by `dotnet ef database update` is typically a
        // superuser/superuser-equivalent in dev, and FORCE is what stops a privileged role
        // from accidentally bypassing RLS by mistake. USING and WITH CHECK reference the same
        // expression so reads and writes are constrained symmetrically (writing a row that
        // doesn't match the ambient account is denied, not just reading). See
        // AddRowLevelSecurity's doc comment on "Policy shape" for the rationale behind the
        // NULLIF(current_setting(...), '')::uuid construction and the safe direction of the
        // unset-GUC fallback.
        migrationBuilder.Sql(
            $"""
            ALTER TABLE "{TargetTable}" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "{TargetTable}" FORCE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS account_scoped ON "{TargetTable}";

            CREATE POLICY account_scoped ON "{TargetTable}"
                USING (
                    current_setting('app.is_admin', true)::boolean
                    OR "AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                )
                WITH CHECK (
                    current_setting('app.is_admin', true)::boolean
                    OR "AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Provider gate mirrors Up. If we were a no-op on the way up, we are a no-op on
        // the way down too.
        if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
        {
            return;
        }

        // Reverse of Up's per-table block. Order matches AddRowLevelSecurity's Down
        // (policy drop before the FORCE / DISABLE flags) so a partial-failure replay
        // leaves the table in the same intermediate state as the parent migration would.
        migrationBuilder.Sql(
            $"""
            DROP POLICY IF EXISTS account_scoped ON "{TargetTable}";
            ALTER TABLE "{TargetTable}" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "{TargetTable}" DISABLE ROW LEVEL SECURITY;
            """);
    }
}