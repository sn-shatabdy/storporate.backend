using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSponsorshipRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SponsorshipRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubOwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyOwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    GoalSetId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClubName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    University = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    GoalName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EventTitle = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    EventDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EventDescription = table.Column<string>(type: "text", nullable: false),
                    Ask = table.Column<string>(type: "text", nullable: false),
                    AmountRequested = table.Column<int>(type: "integer", nullable: true),
                    Offer = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DecisionNote = table.Column<string>(type: "text", nullable: true),
                    OutcomeNote = table.Column<string>(type: "text", nullable: true),
                    AgreedAmount = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ViewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorshipRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorshipRequests_ClubProfiles_ClubProfileId",
                        column: x => x.ClubProfileId,
                        principalTable: "ClubProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SponsorshipRequests_SponsorshipGoalSets_GoalSetId",
                        column: x => x.GoalSetId,
                        principalTable: "SponsorshipGoalSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SponsorshipRequests_Users_ClubOwnerAccountId",
                        column: x => x.ClubOwnerAccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SponsorshipRequests_Users_CompanyOwnerAccountId",
                        column: x => x.CompanyOwnerAccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SponsorshipRequestMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderSide = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorshipRequestMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorshipRequestMessages_SponsorshipRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "SponsorshipRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorshipRequestMessages_RequestId_CreatedAt",
                table: "SponsorshipRequestMessages",
                columns: new[] { "RequestId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorshipRequests_ClubOwnerAccountId_Status",
                table: "SponsorshipRequests",
                columns: new[] { "ClubOwnerAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorshipRequests_ClubProfileId",
                table: "SponsorshipRequests",
                column: "ClubProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_SponsorshipRequests_CompanyOwnerAccountId_Status",
                table: "SponsorshipRequests",
                columns: new[] { "CompanyOwnerAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorshipRequests_GoalSetId",
                table: "SponsorshipRequests",
                column: "GoalSetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SponsorshipRequestMessages");

            migrationBuilder.DropTable(
                name: "SponsorshipRequests");
        }
    }
}
