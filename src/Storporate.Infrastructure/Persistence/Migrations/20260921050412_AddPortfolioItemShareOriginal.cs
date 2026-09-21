using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// STOR-44 Phase 1: lay down the per-item drill-down opt-in column on
    /// <c>PortfolioItems</c>. Students control the column via
    /// <c>PUT /api/portfolio/items/{id}/sharing</c>; the
    /// <c>RefreshTalentIndexEntryProcessor</c> reads it to decide whether the
    /// non-tenant <c>TalentIndexEntries.ItemsJson</c> snapshot should carry an
    /// <c>original</c> descriptor (file / link / etc.) for that item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Back-fill via SQL DEFAULT.</b> The column is non-null on a table that
    /// may already hold rows in production-shape environments. Setting
    /// <c>defaultValue: false</c> on <see cref="AddColumn{T}"/> is what EF Core
    /// emits as <c>DEFAULT false</c> on the <c>ALTER TABLE</c> DDL — Postgres
    /// applies that default to every existing row at column-add time, so a fresh
    /// production deploy lands with every portfolio item in the documented
    /// initial state (<c>shareOriginalWithEmployers = false</c>) without a
    /// separate UPDATE pass. The same default flows to new rows via the
    /// entity's <see cref="Microsoft.EntityFrameworkCore"/> C# property default
    /// (the <c>HasDefaultValue(false)</c> on the EF configuration) so the
    /// application-side insert path stays consistent with the database-side
    /// default.
    /// </para>
    /// <para>
    /// <b>Why not a separate nullable flag with a "true means yes, null means default false"</b>
    /// column. The single non-null boolean is what the plan calls for and what
    /// the refresh processor will branch on; a nullable variant would let a
    /// future "unset means pending review" semantics silently enter the model
    /// and complicate the processor's branch on whether to project a
    /// descriptor.
    /// </para>
    /// </remarks>
    public partial class AddPortfolioItemShareOriginal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShareOriginalWithEmployers",
                table: "PortfolioItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShareOriginalWithEmployers",
                table: "PortfolioItems");
        }
    }
}
