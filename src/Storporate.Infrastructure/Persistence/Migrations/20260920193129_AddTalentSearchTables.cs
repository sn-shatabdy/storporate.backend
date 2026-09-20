using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Storporate.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// STOR-43 Phase 1: create the two new tables for the student-side
    /// plain-language talent search and hand-write the pgvector extension,
    /// the <c>Embedding</c> column on <c>TalentIndexEntries</c>, and the HNSW
    /// cosine-distance index that EF's scaffolding can't emit because the
    /// vector column is intentionally outside the EF model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the <c>vector</c> extension is created here (not the
    /// <c>AddStudentSearchProfileRowLevelSecurity</c> follow-up).</b> The
    /// extension has to exist before the <c>ALTER TABLE ... ADD COLUMN
    /// "Embedding" vector(768)</c> statement runs, so it has to land in the
    /// same migration as the column. <c>CREATE EXTENSION IF NOT EXISTS</c>
    /// is idempotent and safe to re-run, so a re-apply of the migration
    /// against an already-provisioned database is a no-op.
    /// </para>
    /// <para>
    /// <b>Why <c>Embedding</c> is added via raw SQL (not via the EF
    /// scaffolding above).</b> The column type <c>vector(768)</c> is a
    /// pgvector-specific type that has no first-class EF Core mapping in
    /// this codebase. The repository reads/writes it through raw Npgsql
    /// commands on the scalar columns + a parallel vector column via the
    /// raw <c>NpgsqlConnection</c>; EF only sees the scalar columns.
    /// </para>
    /// <para>
    /// <b>Why an HNSW index with <c>vector_cosine_ops</c>.</b> The
    /// <c>RefreshTalentIndexEntryProcessor</c> computes embeddings as
    /// cosine-distance comparisons; the index family that matches the
    /// distance operator the SQL uses (<c>&lt;=&gt;</c>) is the HNSW
    /// index with the <c>vector_cosine_ops</c> opclass. The index is
    /// created against the <c>Embedding</c> column only — Postgres treats
    /// <c>vector</c> as a typed dimension, so the index covers every
    /// existing and future row automatically.
    /// </para>
    /// </remarks>
    public partial class AddTalentSearchTables : Migration
    {
        /// <summary>
        /// Exact provider name
        /// <see cref="Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder,string,System.Action{Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilder})"/>
        /// registers. Compared verbatim against
        /// <see cref="MigrationBuilder.ActiveProvider"/> so the hand-written
        /// pgvector DDL below only fires under Npgsql — under the InMemory
        /// provider the raw SQL would be silently skipped anyway, but the
        /// gate mirrors the prior-art RLS migrations for symmetry.
        /// </summary>
        private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StudentSearchProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsSearchable = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Headline = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    University = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    FieldOfStudy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    StudyYear = table.Column<int>(type: "integer", nullable: true),
                    ShowHeadline = table.Column<bool>(type: "boolean", nullable: false),
                    ShowUniversity = table.Column<bool>(type: "boolean", nullable: false),
                    ShowFieldOfStudy = table.Column<bool>(type: "boolean", nullable: false),
                    ShowStudyYear = table.Column<bool>(type: "boolean", nullable: false),
                    OptedInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudentSearchProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TalentIndexEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Headline = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    University = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    FieldOfStudy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    StudyYear = table.Column<int>(type: "integer", nullable: true),
                    ItemsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SearchText = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TalentIndexEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StudentSearchProfiles_AccountId",
                table: "StudentSearchProfiles",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TalentIndexEntries_StudentAccountId",
                table: "TalentIndexEntries",
                column: "StudentAccountId",
                unique: true);

            // Hand-written DDL: pgvector extension, the Embedding column, and
            // the HNSW cosine-distance index. EF can't emit any of this from
            // the model — the vector type isn't mapped in the EF model by
            // design (the repository reads/writes the column through raw
            // Npgsql commands on a separate connection, keeping EF out of the
            // hot path for vector data).
            if (string.Equals(migrationBuilder.ActiveProvider, NpgsqlProviderName, System.StringComparison.Ordinal))
            {
                migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

                migrationBuilder.Sql(
                    "ALTER TABLE \"TalentIndexEntries\" ADD COLUMN \"Embedding\" vector(768) NOT NULL;");

                migrationBuilder.Sql(
                    "CREATE INDEX \"IX_TalentIndexEntries_Embedding\" ON \"TalentIndexEntries\" USING hnsw (\"Embedding\" vector_cosine_ops);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StudentSearchProfiles");

            migrationBuilder.DropTable(
                name: "TalentIndexEntries");
        }
    }
}
