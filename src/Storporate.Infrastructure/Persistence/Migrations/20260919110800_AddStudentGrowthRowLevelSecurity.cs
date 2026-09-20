using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations;

/// <summary>
/// STOR-40 Phase 1 follow-up: install the same <c>account_scoped</c> PostgreSQL
/// row-level security policy on each of the six new tenant tables that
/// <see cref="AddPortfolioItemsRowLevelSecurity"/> and
/// <see cref="AddPortfolioSkillFindingsRowLevelSecurity"/> install on the
/// Portfolio module's tables. <c>FeedItems</c> is intentionally absent — it
/// is the one global table (not <see cref="SharedKernel.Entities.IAccountScoped"/>),
/// readable by every student and written only by the feed-refresh background
/// service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a single migration covering six tables.</b> The six tables are
/// created together by <see cref="AddStudentGrowthTables"/>, ship in the
/// same release, and are exercised by the same query pipeline (the global
/// query filter, the save-time interceptor, and this RLS policy). Sibling
/// follow-up migrations in this codebase (the Portfolio / PortfolioSkill
/// pair) cover two tables each because they were created across two
/// earlier stories; here the whole batch lands in one story and one
/// migration is enough. Combining them keeps the migration history
/// proportionate to the scope of the change.
/// </para>
/// <para>
/// <b>Idempotency and provider gating.</b> Each table gets
/// <c>DROP POLICY IF EXISTS</c> + <c>CREATE POLICY</c> on the way up so a
/// re-run is a no-op against an already-applied database; on the way down
/// the policy is dropped before the FORCE / DISABLE flags are cleared so a
/// partial-failure replay matches the prior art's intermediate state. The
/// provider gate (<c>ActiveProvider == Npgsql</c>) skips every SQL block
/// under any non-Npgsql provider so the InMemory test suite no-ops cleanly.
/// </para>
/// </remarks>
public partial class AddStudentGrowthRowLevelSecurity : Migration
{
    /// <summary>
    /// Exact provider name <see cref="Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder,string,System.Action{Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilder})"/>
    /// registers. Compared verbatim against <see cref="Migrations.MigrationBuilder.ActiveProvider"/>.
    /// </summary>
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// The six <see cref="SharedKernel.Entities.IAccountScoped"/> tables this
    /// follow-up installs RLS coverage on. Kept as a constant array (rather
    /// than walked from the model) for the same reason
    /// <see cref="AddRowLevelSecurity.AccountScopedTables"/> keeps its array
    /// explicit: the RLS policy is per-table DDL that has to be authored as a
    /// known set, and the migration must be source-controllable without
    /// runtime introspection. <c>FeedItems</c> is deliberately omitted — it
    /// is a global table, not <see cref="SharedKernel.Entities.IAccountScoped"/>,
    /// and would fail this migration if included.
    /// </summary>
    private static readonly string[] AccountScopedTables =
    {
        "Explorations",
        "ExplorationMessages",
        "ExplorationSummaryVersions",
        "StudentContextNotes",
        "ExplorationComparisons",
        "StudentFeedEntries",
    };

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

        // Each table gets the same policy shape: ENABLE + FORCE so the table
        // owner can't accidentally bypass RLS; DROP IF EXISTS + CREATE for
        // idempotency; USING + WITH CHECK referencing the same expression so
        // reads and writes are constrained symmetrically (writing a row
        // with the wrong AccountId is denied, not just reading). See
        // AddRowLevelSecurity's doc comment on "Policy shape" for the
        // rationale behind the NULLIF(current_setting(...), '')::uuid
        // construction and the safe direction of the unset-GUC fallback.
        foreach (var table in AccountScopedTables)
        {
            migrationBuilder.Sql(
                $"""
                ALTER TABLE "{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "{table}" FORCE ROW LEVEL SECURITY;

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
                """);
        }
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

        // Reverse of Up's per-table block. Order matches the prior art's
        // Down (policy drop before the FORCE / DISABLE flags) so a
        // partial-failure replay leaves the table in the same intermediate
        // state as the parent migration would.
        foreach (var table in AccountScopedTables)
        {
            migrationBuilder.Sql(
                $"""
                DROP POLICY IF EXISTS account_scoped ON "{table}";
                ALTER TABLE "{table}" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE "{table}" DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}