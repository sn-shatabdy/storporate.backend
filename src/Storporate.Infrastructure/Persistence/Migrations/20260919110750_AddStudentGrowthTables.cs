using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentGrowthTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Explorations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Explorations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Explorations_Users_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FeedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExplorationComparisons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstExplorationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecondExplorationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResultText = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExplorationComparisons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExplorationComparisons_Explorations_FirstExplorationId",
                        column: x => x.FirstExplorationId,
                        principalTable: "Explorations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExplorationComparisons_Explorations_SecondExplorationId",
                        column: x => x.SecondExplorationId,
                        principalTable: "Explorations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExplorationComparisons_Users_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExplorationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExplorationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    QuestionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExplorationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExplorationMessages_Explorations_ExplorationId",
                        column: x => x.ExplorationId,
                        principalTable: "Explorations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExplorationMessages_Users_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExplorationSummaryVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExplorationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    GapsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SuggestionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ChangeNote = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExplorationSummaryVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExplorationSummaryVersions_Explorations_ExplorationId",
                        column: x => x.ExplorationId,
                        principalTable: "Explorations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExplorationSummaryVersions_Users_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StudentContextNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExplorationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentContextNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentContextNotes_Explorations_ExplorationId",
                        column: x => x.ExplorationId,
                        principalTable: "Explorations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentContextNotes_Users_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StudentFeedEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeedItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentFeedEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StudentFeedEntries_FeedItems_FeedItemId",
                        column: x => x.FeedItemId,
                        principalTable: "FeedItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StudentFeedEntries_Users_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExplorationComparisons_AccountId",
                table: "ExplorationComparisons",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ExplorationComparisons_AccountId_CreatedAt",
                table: "ExplorationComparisons",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExplorationComparisons_FirstExplorationId",
                table: "ExplorationComparisons",
                column: "FirstExplorationId");

            migrationBuilder.CreateIndex(
                name: "IX_ExplorationComparisons_SecondExplorationId",
                table: "ExplorationComparisons",
                column: "SecondExplorationId");

            migrationBuilder.CreateIndex(
                name: "IX_ExplorationMessages_AccountId",
                table: "ExplorationMessages",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ExplorationMessages_ExplorationId_CreatedAt",
                table: "ExplorationMessages",
                columns: new[] { "ExplorationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Explorations_AccountId",
                table: "Explorations",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Explorations_AccountId_UpdatedAt",
                table: "Explorations",
                columns: new[] { "AccountId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExplorationSummaryVersions_AccountId",
                table: "ExplorationSummaryVersions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ExplorationSummaryVersions_ExplorationId_VersionNumber",
                table: "ExplorationSummaryVersions",
                columns: new[] { "ExplorationId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_PublishedAt_FetchedAt",
                table: "FeedItems",
                columns: new[] { "PublishedAt", "FetchedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedItems_Url",
                table: "FeedItems",
                column: "Url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentContextNotes_AccountId",
                table: "StudentContextNotes",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentContextNotes_AccountId_CreatedAt",
                table: "StudentContextNotes",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StudentContextNotes_ExplorationId",
                table: "StudentContextNotes",
                column: "ExplorationId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentFeedEntries_AccountId",
                table: "StudentFeedEntries",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_StudentFeedEntries_AccountId_FeedItemId",
                table: "StudentFeedEntries",
                columns: new[] { "AccountId", "FeedItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StudentFeedEntries_FeedItemId",
                table: "StudentFeedEntries",
                column: "FeedItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExplorationComparisons");

            migrationBuilder.DropTable(
                name: "ExplorationMessages");

            migrationBuilder.DropTable(
                name: "ExplorationSummaryVersions");

            migrationBuilder.DropTable(
                name: "StudentContextNotes");

            migrationBuilder.DropTable(
                name: "StudentFeedEntries");

            migrationBuilder.DropTable(
                name: "Explorations");

            migrationBuilder.DropTable(
                name: "FeedItems");
        }
    }
}
