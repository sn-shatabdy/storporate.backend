using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations;

/// <summary>
/// STOR-43 Phase 1 follow-up: install the <c>account_scoped</c> PostgreSQL
/// row-level security policy on <c>StudentSearchProfiles</c>, the new
/// opt-in profile table that ships in <c>AddTalentSearchTables</c> and
/// implements <see cref="SharedKernel.Entities.IAccountScoped"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate follow-up migration (rather than folding the RLS
/// block into <c>AddTalentSearchTables</c>).</b> The prior art — see
/// <see cref="AddPortfolioItemsRowLevelSecurity"/>,
/// <see cref="AddPortfolioSkillFindingsRowLevelSecurity"/>, and
/// <see cref="AddStudentGrowthRowLevelSecurity"/> — ships each RLS
/// install as a dedicated follow-up migration so the auto-generated
/// <c>CreateTable</c> migration stays a clean snapshot of the EF model
/// while the hand-written follow-up carries the DDL that has to read the
/// <c>current_setting(...)</c> GUC the <c>RowLevelSecurityInterceptor</c>
/// refreshes. Following the same shape keeps the migration history
/// consistent and grep-able: every <c>account_scoped</c> policy install
/// lives in a <c>*RowLevelSecurity.cs</c> file with a matching timestamp.
/// </para>
/// <para>
/// <b>Why <c>TalentIndexEntries</c> is absent.</b> It is the single
/// non-<see cref="SharedKernel.Entities.IAccountScoped"/> table introduced
/// by this story: a global employer-facing index with no <c>AccountId</c>
/// column on the row itself (the student's <c>User.Id</c> is the natural
/// key, but the table is read across every account). The RLS contract in
/// this codebase applies to <see cref="SharedKernel.Entities.IAccountScoped"/>
/// tables; <c>TalentIndexEntries</c> by design has no such row, so
/// installing a policy against it would either always-allow (no
/// <c>AccountId</c> to compare against) or always-deny, neither of
/// which matches the table's role.
/// </para>
/// <para>
/// <b>Idempotency and provider gating.</b> <c>DROP POLICY IF EXISTS</c>
/// + <c>CREATE POLICY</c> on the way up; <c>DROP POLICY</c> + clear the
/// <c>FORCE</c> / <c>ENABLE</c> flags on the way down, mirroring the
/// prior art so a partial-failure replay leaves the table in the same
/// intermediate state as the parent <c>CreateTable</c> migration. The
/// provider gate (<c>ActiveProvider == Npgsql</c>) skips every SQL block
/// under any non-Npgsql provider so the InMemory test suite no-ops
/// cleanly.
/// </para>
/// </remarks>
public partial class AddStudentSearchProfileRowLevelSecurity : Migration
{
    /// <summary>
    /// Exact provider name <see cref="Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder,string,System.Action{Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilder})"/>
    /// registers. Compared verbatim against <see cref="Migrations.MigrationBuilder.ActiveProvider"/>.
    /// </summary>
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// The single <see cref="SharedKernel.Entities.IAccountScoped"/> table this
    /// follow-up installs RLS coverage on. Kept as a constant (rather than
    /// walked from the model) for the same reason
    /// <see cref="AddRowLevelSecurity.AccountScopedTables"/> keeps its array
    /// explicit: the RLS policy is per-table DDL that has to be authored as
    /// a known set, and the migration must be source-controllable without
    /// runtime introspection.
    /// </summary>
    private const string AccountScopedTable = "StudentSearchProfiles";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Provider gate — mirrors the prior-art migrations. Under any
        // non-Npgsql provider (the InMemory test suite, for example) this
        // migration is a no-op and every SQL block below is skipped.
        if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
        {
            return;
        }

        // Same policy shape as AddRowLevelSecurity / AddPortfolioItemsRowLevelSecurity /
        // AddStudentGrowthRowLevelSecurity: ENABLE + FORCE so the table
        // owner can't accidentally bypass RLS; DROP IF EXISTS + CREATE for
        // idempotency; USING + WITH CHECK referencing the same expression
        // so reads and writes are constrained symmetrically (writing a row
        // with the wrong AccountId is denied, not just reading). See
        // AddRowLevelSecurity's doc comment on "Policy shape" for the
        // rationale behind the NULLIF(current_setting(...), '')::uuid
        // construction and the safe direction of the unset-GUC fallback.
        migrationBuilder.Sql(
            $"""
            ALTER TABLE "{AccountScopedTable}" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "{AccountScopedTable}" FORCE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS account_scoped ON "{AccountScopedTable}";

            CREATE POLICY account_scoped ON "{AccountScopedTable}"
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
        // Provider gate mirrors Up: a no-op on the way up is a no-op on
        // the way down too.
        if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
        {
            return;
        }

        // Reverse of Up's block. Order matches the prior art's Down
        // (policy drop before the FORCE / DISABLE flags) so a
        // partial-failure replay leaves the table in the same intermediate
        // state as the parent CreateTable migration would.
        migrationBuilder.Sql(
            $"""
            DROP POLICY IF EXISTS account_scoped ON "{AccountScopedTable}";
            ALTER TABLE "{AccountScopedTable}" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "{AccountScopedTable}" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
