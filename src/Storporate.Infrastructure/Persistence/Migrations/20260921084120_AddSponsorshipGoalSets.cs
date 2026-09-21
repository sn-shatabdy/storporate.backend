using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSponsorshipGoalSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SponsorshipGoalSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Objectives = table.Column<string>(type: "text", nullable: false),
                    AudienceFieldsOfStudy = table.Column<string>(type: "text", nullable: false),
                    AudienceYears = table.Column<string>(type: "text", nullable: false),
                    AudienceCities = table.Column<string>(type: "text", nullable: false),
                    AudienceUniversities = table.Column<string>(type: "text", nullable: false),
                    EventKinds = table.Column<string>(type: "text", nullable: false),
                    BudgetMin = table.Column<int>(type: "integer", nullable: true),
                    BudgetMax = table.Column<int>(type: "integer", nullable: true),
                    ShowBudget = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorshipGoalSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorshipGoalSets_Users_OwnerAccountId",
                        column: x => x.OwnerAccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorshipGoalSets_OwnerAccountId",
                table: "SponsorshipGoalSets",
                column: "OwnerAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SponsorshipGoalSets_Status_CreatedAt",
                table: "SponsorshipGoalSets",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SponsorshipGoalSets");
        }
    }
}
