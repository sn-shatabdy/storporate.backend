using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations;

/// <summary>
/// STOR-43 Phase 2 follow-up: install the <c>account_scoped</c> PostgreSQL
/// row-level security policy on <c>TalentSearchRequests</c>, the new
/// Organization-side search record that ships in
/// <c>AddTalentSearchRequests</c> and implements
/// <see cref="Storporate.SharedKernel.Entities.IAccountScoped"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate follow-up migration (rather than folding the RLS
/// block into <c>AddTalentSearchRequests</c>).</b> Same rationale as
/// <see cref="AddStudentSearchProfileRowLevelSecurity"/> and every prior
/// <c>*RowLevelSecurity</c> migration: the auto-generated
/// <c>CreateTable</c> migration stays a clean snapshot of the EF model
/// while the hand-written follow-up carries the DDL that has to read the
/// <c>current_setting(...)</c> GUC the <c>RowLevelSecurityInterceptor</c>
/// refreshes.
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
public partial class AddTalentSearchRequestRowLevelSecurity : Migration
{
    /// <summary>
    /// Exact provider name <see cref="Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder,string,System.Action{Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilder})"/>
    /// registers. Compared verbatim against <see cref="Migrations.MigrationBuilder.ActiveProvider"/>.
    /// </summary>
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// The single <see cref="Storporate.SharedKernel.Entities.IAccountScoped"/>
    /// table this follow-up installs RLS coverage on.
    /// </summary>
    private const string AccountScopedTable = "TalentSearchRequests";

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
        // AddStudentSearchProfileRowLevelSecurity: ENABLE + FORCE so the table
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
