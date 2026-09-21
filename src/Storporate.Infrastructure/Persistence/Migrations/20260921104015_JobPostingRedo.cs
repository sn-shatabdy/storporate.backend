using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JobPostingRedo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // STOR-66 Phase 1: complete the job-postings data model for a real hiring flow.
            //
            // 1. Resize Title from 200 to 120 to match the validator's hard cap.
            // 2. Add the four new columns the orchestrator depends on
            //    (deadline, openings, compensation band, visibility flag).
            // 3. Add SearchText and backfill it from existing rows so the
            //    browse endpoint's word-substring match has something to find
            //    on rows that pre-date the redo.
            // 4. Add CHECK constraints on Kind / WorkMode / Status to match the
            //    validator's allowlists and stop bad rows reaching the table.
            // 5. Add the xmin concurrency token mapping for Npgsql read-modify-write
            //    protection on update / status change.

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "JobPostings",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ApplicationDeadline",
                table: "JobPostings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompensationMax",
                table: "JobPostings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompensationMin",
                table: "JobPostings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Openings",
                table: "JobPostings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "JobPostings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ShowCompensation",
                table: "JobPostings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "JobPostings",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            // Backfill SearchText from the existing columns. The build matches
            // JobPostingSkills.BuildSearchText: lower-case of title, company,
            // location, and the JSON skills array with brackets / quotes /
            // commas replaced by single spaces (no punctuation to match).
            migrationBuilder.Sql(@"
                UPDATE ""JobPostings""
                SET ""SearchText"" = lower(regexp_replace(
                    COALESCE(""Title"", '') || ' '
                        || COALESCE(""CompanyName"", '') || ' '
                        || COALESCE(""Location"", '') || ' '
                        || COALESCE(regexp_replace(COALESCE(""RequiredSkills"", ''), '[\[\]""\,]', ' ', 'g'), ''),
                    '[^\w]+', ' ', 'g'))
                WHERE ""SearchText"" = '';
            ");

            // CHECK constraints: enforce the same allowlists the validators use.
            migrationBuilder.Sql(@"
                ALTER TABLE ""JobPostings""
                ADD CONSTRAINT ""CK_JobPostings_Kind""
                CHECK (""Kind"" IN ('Job', 'Internship'));
            ");
            migrationBuilder.Sql(@"
                ALTER TABLE ""JobPostings""
                ADD CONSTRAINT ""CK_JobPostings_WorkMode""
                CHECK (""WorkMode"" IN ('OnSite', 'Remote', 'Hybrid'));
            ");
            migrationBuilder.Sql(@"
                ALTER TABLE ""JobPostings""
                ADD CONSTRAINT ""CK_JobPostings_Status""
                CHECK (""Status"" IN ('Open', 'Paused', 'Closed'));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""JobPostings"" DROP CONSTRAINT IF EXISTS ""CK_JobPostings_Status"";");
            migrationBuilder.Sql(@"ALTER TABLE ""JobPostings"" DROP CONSTRAINT IF EXISTS ""CK_JobPostings_WorkMode"";");
            migrationBuilder.Sql(@"ALTER TABLE ""JobPostings"" DROP CONSTRAINT IF EXISTS ""CK_JobPostings_Kind"";");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "ShowCompensation",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "Openings",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "CompensationMin",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "CompensationMax",
                table: "JobPostings");

            migrationBuilder.DropColumn(
                name: "ApplicationDeadline",
                table: "JobPostings");

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "JobPostings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120);
        }
    }
}
