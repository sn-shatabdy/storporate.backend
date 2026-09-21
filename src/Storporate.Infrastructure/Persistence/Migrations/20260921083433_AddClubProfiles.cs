using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClubProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClubProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Tagline = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    About = table.Column<string>(type: "text", nullable: false),
                    University = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FoundedYear = table.Column<int>(type: "integer", nullable: true),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    AudienceFieldsOfStudy = table.Column<string>(type: "text", nullable: false),
                    AudienceYears = table.Column<string>(type: "text", nullable: false),
                    Events = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClubProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClubProfiles_Users_OwnerAccountId",
                        column: x => x.OwnerAccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClubProfiles_OwnerAccountId",
                table: "ClubProfiles",
                column: "OwnerAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClubProfiles_Status_University",
                table: "ClubProfiles",
                columns: new[] { "Status", "University" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClubProfiles");
        }
    }
}
