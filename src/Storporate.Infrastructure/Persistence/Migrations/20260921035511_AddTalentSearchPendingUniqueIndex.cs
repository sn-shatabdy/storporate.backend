using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations;

/// <summary>
/// STOR-43 Cross-Validation Step 5 (security review, item 7):
/// installs a partial unique index on <c>TalentSearchRequests</c> so
/// two concurrent <c>POST /api/discovery/talent-searches</c> calls
/// from the same Organization cannot both succeed when one
/// <see cref="TalentSearchStatuses.Pending"/> row already exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this fixes.</b> The original index
/// <c>IX_TalentSearchRequests_AccountId_Status</c> is non-unique.
/// <see cref="CreateTalentSearchHandler"/> therefore runs a TOCTOU
/// <c>AnyAsync(Status==Pending)</c> check followed by an
/// <c>Add + SaveChanges</c>; under concurrency both POSTs pass the
/// pre-check and both insert, leaving two in-flight searches for the
/// same Organization. The handler now catches Postgres error
/// <c>23505</c> on the unique index and translates it to
/// <c>TalentSearchBusyException</c> (HTTP 409). The pre-check still
/// runs as the fast path for the common sequential case.
/// </para>
/// <para>
/// <b>Partial.</b> The <c>WHERE "Status" = 'Pending'</c> clause lets
/// every Organization have any number of Completed / Failed rows —
/// only the active row is uniquely constrained. All other table state
/// is unchanged.
/// </para>
/// <para>
/// <b>Down semantics.</b> Drops the partial unique index and restores
/// the original non-unique composite so a
/// <c>database update PreviousMigration</c> is a clean reversal.
/// </para>
/// </remarks>
public partial class AddTalentSearchPendingUniqueIndex : Migration
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
        {
            return;
        }

        migrationBuilder.Sql(
            """
            -- Drop the non-unique composite added by AddTalentSearchRequests
            -- so the partial unique index below can claim the prefix slot
            -- without leaving a redundant non-unique copy behind.
            DROP INDEX IF EXISTS "IX_TalentSearchRequests_AccountId_Status";

            CREATE UNIQUE INDEX "UX_TalentSearchRequests_AccountId_Pending"
                ON "TalentSearchRequests" ("AccountId")
                WHERE "Status" = 'Pending';
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (!string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
        {
            return;
        }

        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS "UX_TalentSearchRequests_AccountId_Pending";

            CREATE INDEX "IX_TalentSearchRequests_AccountId_Status"
                ON "TalentSearchRequests" ("AccountId", "Status");
            """);
    }
}
