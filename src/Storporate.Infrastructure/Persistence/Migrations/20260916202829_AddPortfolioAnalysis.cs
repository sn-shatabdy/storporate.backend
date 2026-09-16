using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// STOR-38 Phase 1: lay down the data model the Phase 2 background worker and Phase 3
    /// per-item analysis endpoint will read/write against. Adds the per-item analysis-state
    /// columns to <c>PortfolioItems</c> and introduces the <c>PortfolioSkillFindings</c>
    /// table that holds one row per (item, skill) the worker extracts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>AnalysisStatus back-fill.</b> The new <c>AnalysisStatus</c> column is non-null on a
    /// table that already has rows in production-shape environments. The
    /// <c>defaultValue: "NotAnalyzed"</c> on <c>AddColumn</c> is what EF Core emits as a
    /// <c>DEFAULT "NotAnalyzed"</c> clause on the <c>ALTER TABLE ... ADD COLUMN</c> DDL —
    /// Postgres applies that default to every existing row at column-add time, so a fresh
    /// production deploy lands with all existing portfolio items in the documented initial
    /// state (<c>NotAnalyzed</c>) without needing a separate UPDATE pass. The same default
    /// flows to new rows via the entity's C# property initializer
    /// (<c>PortfolioItem.AnalysisStatus = PortfolioAnalysisStatuses.NotAnalyzed</c>) so the
    /// application-side insert path stays consistent with the database-side default.
    /// </para>
    /// <para>
    /// <b>Skill-finding table.</b> <c>PortfolioSkillFindings</c> is a child of
    /// <c>PortfolioItems</c> with a <see cref="ReferentialAction.Restrict"/> delete
    /// (matching the existing <c>Jobs</c> / <c>PortfolioItems</c> FK style) so the
    /// application's hard-delete pipeline can't silently orphan findings — the Phase 2
    /// worker is responsible for clearing findings before the parent item itself can be
    /// deleted. <c>AccountId</c> is carried on the child for the same reason
    /// <c>Jobs.AccountId</c> is: the EF Core global query filter and the Postgres RLS
    /// policy both key on <c>AccountId</c> directly, no join required.
    /// </para>
    /// <para>
    /// <b>Row-level security.</b> The <c>account_scoped</c> RLS policy is <i>not</i>
    /// installed in this migration — it's added by the follow-up migration
    /// <c>AddPortfolioSkillFindingsRowLevelSecurity</c>, mirroring the split between
    /// <c>AddPortfolioItems</c> (this story) and <c>AddPortfolioItemsRowLevelSecurity</c>
    /// (STOR-37 Phase 1 follow-up). The split is the documented layering pattern for any
    /// new <c>IAccountScoped</c> entity: a schema migration, then a separate
    /// provider-gated RLS migration that runs only under Npgsql.
    /// </para>
    /// </remarks>
    public partial class AddPortfolioAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnalysisStatus",
                table: "PortfolioItems",
                type: "text",
                nullable: false,
                defaultValue: "NotAnalyzed");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAnalyzedAt",
                table: "PortfolioItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PortfolioSkillFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ConfidenceBand = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Explanation = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioSkillFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioSkillFindings_PortfolioItems_PortfolioItemId",
                        column: x => x.PortfolioItemId,
                        principalTable: "PortfolioItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PortfolioSkillFindings_Users_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioSkillFindings_AccountId",
                table: "PortfolioSkillFindings",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioSkillFindings_PortfolioItemId",
                table: "PortfolioSkillFindings",
                column: "PortfolioItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortfolioSkillFindings");

            migrationBuilder.DropColumn(
                name: "LastAnalyzedAt",
                table: "PortfolioItems");

            migrationBuilder.DropColumn(
                name: "AnalysisStatus",
                table: "PortfolioItems");
        }
    }
}
