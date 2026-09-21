using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutreach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutreachConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    StudentDisplayName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutreachConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutreachConversations_Users_OrganizationAccountId",
                        column: x => x.OrganizationAccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OutreachConversations_Users_StudentAccountId",
                        column: x => x.StudentAccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShortlistEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShortlistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShortlistEntries_Users_OrganizationAccountId",
                        column: x => x.OrganizationAccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShortlistEntries_Users_StudentAccountId",
                        column: x => x.StudentAccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutreachMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderRole = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutreachMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutreachMessages_OutreachConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "OutreachConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutreachConversations_OrganizationAccountId",
                table: "OutreachConversations",
                column: "OrganizationAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OutreachConversations_OrganizationAccountId_StudentAccountId",
                table: "OutreachConversations",
                columns: new[] { "OrganizationAccountId", "StudentAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutreachConversations_StudentAccountId",
                table: "OutreachConversations",
                column: "StudentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OutreachMessages_ConversationId_CreatedAt",
                table: "OutreachMessages",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShortlistEntries_OrganizationAccountId",
                table: "ShortlistEntries",
                column: "OrganizationAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ShortlistEntries_OrganizationAccountId_StudentAccountId",
                table: "ShortlistEntries",
                columns: new[] { "OrganizationAccountId", "StudentAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShortlistEntries_StudentAccountId",
                table: "ShortlistEntries",
                column: "StudentAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutreachMessages");

            migrationBuilder.DropTable(
                name: "ShortlistEntries");

            migrationBuilder.DropTable(
                name: "OutreachConversations");
        }
    }
}
