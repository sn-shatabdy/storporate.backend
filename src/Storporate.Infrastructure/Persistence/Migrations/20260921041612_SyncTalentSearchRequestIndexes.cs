using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations;

/// <summary>
/// STOR-43 Cross-Validation Step 5 (H3): sync the EF Core model for
/// <see cref="TalentSearchRequest"/> to the index shape installed by
/// <c>AddTalentSearchPendingUniqueIndex</c>. The original model declared
/// the dropped non-unique composite <c>IX_TalentSearchRequests_AccountId_Status</c>
/// and a redundant non-unique <c>IX_TalentSearchRequests_AccountId</c>; the
/// live database now carries the partial unique
/// <c>UX_TalentSearchRequests_AccountId_Pending</c> (one Pending row
/// per Organization). The model now declares the index under the name
/// <c>IX_TalentSearchRequests_AccountId</c>; this migration renames the
/// partial unique index idempotently so future designer-driven
/// migrations see a single consistent name. Down reverses the rename
/// without re-creating the dropped composite index on purpose (that
/// index was dropped by <c>AddTalentSearchPendingUniqueIndex</c>;
/// recreating it would silently undo the busy-guard fix).
/// </summary>
public partial class SyncTalentSearchRequestIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM pg_indexes
                    WHERE schemaname = current_schema()
                      AND tablename = 'TalentSearchRequests'
                      AND indexname = 'UX_TalentSearchRequests_AccountId_Pending'
                ) THEN
                    DROP INDEX IF EXISTS "IX_TalentSearchRequests_AccountId";
                    ALTER INDEX "UX_TalentSearchRequests_AccountId_Pending"
                        RENAME TO "IX_TalentSearchRequests_AccountId";
                END IF;
            END $$;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM pg_indexes
                    WHERE schemaname = current_schema()
                      AND tablename = 'TalentSearchRequests'
                      AND indexname = 'IX_TalentSearchRequests_AccountId'
                ) THEN
                    ALTER INDEX "IX_TalentSearchRequests_AccountId"
                        RENAME TO "UX_TalentSearchRequests_AccountId_Pending";
                END IF;
            END $$;
            """);
    }
}
