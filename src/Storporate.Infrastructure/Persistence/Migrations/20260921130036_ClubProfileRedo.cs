using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClubProfileRedo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // STOR-69 Phase 1: race-safe first save + concurrency token on update.
            //
            // Adds the Postgres xmin concurrency token mapping to ClubProfiles so
            // EF Core emits a WHERE xmin = @p_xmin clause on UPDATE and surfaces
            // DbUpdateConcurrencyException when the second writer's loaded token
            // no longer matches the live row. ManageClubProfileHandler.SaveAsync
            // catches that and the equivalent unique-violation race on
            // IX_ClubProfiles_OwnerAccountId, throwing ClubProfileConflictException
            // → HTTP 409 club_profile_conflict.

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "ClubProfiles",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "ClubProfiles");
        }
    }
}
