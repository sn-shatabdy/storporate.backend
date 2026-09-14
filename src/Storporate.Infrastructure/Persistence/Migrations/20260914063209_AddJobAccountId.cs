using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "Jobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_AccountId",
                table: "Jobs",
                column: "AccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_Users_AccountId",
                table: "Jobs",
                column: "AccountId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_Users_AccountId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_AccountId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Jobs");
        }
    }
}
