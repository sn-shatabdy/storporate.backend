using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Migrations;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Architecture;

/// <summary>
/// STOR-40 Phase 1: build-failing coverage gate ensuring every
/// <see cref="IAccountScoped"/> entity's table has an
/// <c>account_scoped</c> Postgres row-level security policy installed by some
/// migration in the project's EF migration history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> STOR-62 documented the three-layer isolation
/// pipeline: EF Core global query filter, save-time interceptor, Postgres
/// RLS policy. Two of those three layers are caught by the
/// <see cref="WriteDbContext.OnModelCreating"/> walk over
/// <see cref="IAccountScoped"/> types — a new entity that implements the
/// interface is automatically wired into the query filter and the
/// interceptor. The third layer (Postgres RLS) is a per-table DDL
/// statement in a hand-written migration; nothing in the build forces a
/// future contributor to write that DDL. A new entity that implements
/// <see cref="IAccountScoped"/> but ships without an RLS migration would
/// be silently missing the database-side guard until someone notices a
/// production Postgres log. This test makes that omission build-failing.
/// </para>
/// <para>
/// <b>How it works.</b> Walks every concrete <see cref="Migration"/>
/// subclass in <see cref="Storporate.Infrastructure.Persistence.Migrations"/>,
/// instantiates it against a <see cref="MigrationBuilder"/> whose
/// <see cref="MigrationBuilder.ActiveProvider"/> is the Npgsql provider
/// name, calls <c>Up</c>, and scans the resulting
/// <see cref="MigrationOperation"/> list for any operation that names a
/// target table + emits a <c>CREATE POLICY account_scoped ON ...</c>
/// statement. Then asserts the union of "tables protected by some
/// migration" is a superset of every <see cref="IAccountScoped"/> table.
/// </para>
/// <para>
/// <b>Why reflection over the migration assembly.</b> The migration list is
/// the source of truth for what gets applied to Postgres. Reflecting over
/// the assembly gives us every migration, including future ones, with no
/// hand-maintained list to drift out of sync. The pre-filter on
/// <see cref="Migration"/>-derived types keeps the walk cheap.
/// </para>
/// <para>
/// <b>Why this lives in <c>Tests.Architecture</c>.</b> Same rationale as
/// <see cref="PermissionCoverageTests"/> — a build-failing structural gate
/// on the codebase's isolation story, not a unit test of any one class.
/// </para>
/// </remarks>
public class AccountScopedRowLevelSecurityTests
{
    /// <summary>
    /// Exact provider name <see cref="NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql"/>
    /// registers. Compared verbatim against
    /// <see cref="MigrationBuilder.ActiveProvider"/> so the provider gate
    /// inside each migration's <c>Up</c> (mirroring the
    /// <c>AddRowLevelSecurity</c> / <c>AddPortfolioItemsRowLevelSecurity</c>
    /// / <c>AddPortfolioSkillFindingsRowLevelSecurity</c> pattern) emits its
    /// SQL under this builder and the scan below can see it.
    /// </summary>
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    [Fact]
    public void EveryAccountScopedEntityTable_HasAccountScopedPolicy_InSomeMigration()
    {
        var protectedTables = ScanProtectedTablesFromMigrations();

        var expectedTables = GetAccountScopedTableNames();
        var missing = expectedTables
            .Except(protectedTables, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "The following IAccountScoped tables have no 'account_scoped' Postgres "
            + "row-level security policy installed by any migration. Add a follow-up "
            + "migration (modeled on AddPortfolioSkillFindingsRowLevelSecurity / "
            + "AddStudentGrowthRowLevelSecurity) that runs CREATE POLICY account_scoped "
            + "ON \"<TableName>\" against the new table, then re-run this gate. "
            + "Tables missing coverage:\n  - "
            + string.Join("\n  - ", missing));
    }

    /// <summary>
    /// Regression guard for the gate itself. Walks the migration list,
    /// asserts <c>Jobs</c> appears in the protected set (the very first
    /// table the Phase 5 migration installed RLS on), and that the
    /// <c>AddStudentGrowthRowLevelSecurity</c> migration contributes at
    /// least one of the six new STOR-40 tenant tables. Catches a future
    /// refactor of <see cref="ScanProtectedTablesFromMigrations"/> that
    /// silently stops seeing SQL operations.
    /// </summary>
    [Fact]
    public void CoverageGate_FindsPolicyInstallations_InKnownMigrations()
    {
        var protectedTables = ScanProtectedTablesFromMigrations();

        // The Phase 5 STOR-62 migration installed RLS on Jobs first; a
        // regression that drops Jobs from the protected set means the
        // scanner can no longer see the policy block.
        Assert.Contains("Jobs", protectedTables);

        // At least one of the six STOR-40 tenant tables should be present.
        // Using the full six-table check above would be redundant — this
        // regression test is about "the scanner is wired up at all", not
        // "the full set is present" (the full-set assertion is the primary
        // test above).
        Assert.Contains(
            new[] { "Explorations", "ExplorationMessages", "ExplorationSummaryVersions",
                    "StudentContextNotes", "ExplorationComparisons", "StudentFeedEntries" },
            table => protectedTables.Contains(table));
    }

    /// <summary>
    /// Walks every <see cref="Migration"/> subclass in
    /// <see cref="Storporate.Infrastructure.Persistence.Migrations"/>,
    /// invokes <c>Up</c> against a <see cref="MigrationBuilder"/> whose
    /// <c>ActiveProvider</c> is <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>,
    /// and returns the set of table names that have a
    /// <c>CREATE POLICY account_scoped ON "&lt;Table&gt;"</c> operation
    /// somewhere in their emitted SQL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every migration in this codebase is either an auto-generated
    /// <c>CreateTable</c>-shaped migration (which carries no RLS SQL) or a
    /// hand-written follow-up migration whose <c>Up</c> body emits one
    /// or more <see cref="SqlOperation"/> blocks. We only have to scan
    /// <see cref="SqlOperation.Sql"/> strings; the auto-generated ones
    /// contribute nothing because they never reference
    /// <c>CREATE POLICY</c>.
    /// </para>
    /// <para>
    /// The <c>CREATE POLICY account_scoped ON "&lt;Table&gt;"</c> substring
    /// is the contract every follow-up migration in this project follows.
    /// A future contributor who writes the policy under a different name
    /// (or quotes it differently) will fail this gate, which is the
    /// desired outcome — the contract is named "account_scoped" for
    /// documentation grep-ability and the Phase 5 RLS migration's
    /// <see cref="Microsoft.EntityFrameworkCore.Migrations.Migration.Sql(string)"/>
    /// calls establish the form.
    /// </para>
    /// </remarks>
    private static HashSet<string> ScanProtectedTablesFromMigrations()
    {
        var protectedTables = new HashSet<string>(StringComparer.Ordinal);

        var migrationAssembly = typeof(AddRowLevelSecurity).Assembly;
        var migrationType = typeof(Migration);

        foreach (var type in migrationAssembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !migrationType.IsAssignableFrom(type))
            {
                continue;
            }

            // Skip the design-time ModelSnapshot — it derives from ModelSnapshot,
            // not Migration, so the IsAssignableFrom filter above already
            // excludes it. Defensive check anyway.
            if (typeof(ModelSnapshot).IsAssignableFrom(type))
            {
                continue;
            }

            Migration migration;
            try
            {
                migration = (Migration)Activator.CreateInstance(type)!;
            }
            catch
            {
                // Some migrations require constructor arguments (rare). Skip
                // rather than failing the whole gate; a future contributor
                // who introduces such a migration will trip a follow-up
                // build break when the gate cannot find policies it expects.
                continue;
            }

            var builder = new MigrationBuilder(NpgsqlProviderName);
            InvokeUp(migration, builder);

            foreach (var operation in builder.Operations)
            {
                if (operation is not SqlOperation sqlOperation)
                {
                    continue;
                }

                ExtractProtectedTableNames(sqlOperation.Sql, protectedTables);
            }
        }

        return protectedTables;
    }

    /// <summary>
    /// Scan a single SQL string for every
    /// <c>CREATE POLICY account_scoped ON "&lt;Table&gt;"</c> it contains
    /// and add each table name to <paramref name="destination"/>. Matching
    /// is case-insensitive because Postgres normalizes identifiers but the
    /// emitted SQL preserves the casing the migration source uses.
    /// </summary>
    /// <remarks>
    /// The regex is intentionally narrow: it captures the exact substring
    /// every follow-up migration in this codebase emits. Anything outside
    /// that shape (a renamed policy, a different table-quoting style) is
    /// intentionally not picked up — that's the point of the gate.
    /// </remarks>
    private static void ExtractProtectedTableNames(string? sql, HashSet<string> destination)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return;
        }

        // CREATE POLICY account_scoped ON "<Table>" (case-insensitive, allowing
        // the table name to contain alphanumerics and underscores — exactly
        // what the table-name mapping in this codebase produces).
        var match = System.Text.RegularExpressions.Regex.Match(
            sql,
            @"CREATE\s+POLICY\s+account_scoped\s+ON\s+""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        while (match.Success)
        {
            destination.Add(match.Groups[1].Value);
            match = match.NextMatch();
        }
    }

    /// <summary>
    /// Walk every <see cref="IAccountScoped"/>-implementing CLR type the
    /// EF model knows about and return its mapped table name. Done
    /// reflectively (rather than hard-coding the table names) so a future
    /// <see cref="IAccountScoped"/> entity added with its own
    /// <c>builder.ToTable(...)</c> mapping is picked up automatically and
    /// the gate fails if its RLS coverage wasn't added at the same time.
    /// </summary>
    private static List<string> GetAccountScopedTableNames()
    {
        // Use the production WriteDbContext's model to walk the entities.
        // The InMemory provider is sufficient — it builds the same model
        // shape (configurations, query filters, table mappings) as the
        // Npgsql provider, but without needing a live Postgres.
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase($"AccountScopedTableScan_{Guid.NewGuid()}")
            .Options;

        using var context = new WriteDbContext(options, new StaticAccountContext());
        var tableNames = new List<string>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (clrType is null || !typeof(IAccountScoped).IsAssignableFrom(clrType))
            {
                continue;
            }

            // entityType.GetTableName() returns the configured table name
            // (via builder.ToTable("...")) — for entities without an
            // explicit ToTable call, it returns the CLR type name. Every
            // IAccountScoped entity in this codebase configures its table
            // explicitly via its IEntityTypeConfiguration, so the names
            // returned here are the database-side names.
            var tableName = entityType.GetTableName();
            if (!string.IsNullOrEmpty(tableName))
            {
                tableNames.Add(tableName);
            }
        }

        return tableNames;
    }

    /// <summary>
    /// Invoke the protected <see cref="Migration.Up"/> override on <paramref name="migration"/>
    /// reflectively. Each concrete migration in this codebase declares its own
    /// <c>protected override void Up(MigrationBuilder migrationBuilder)</c> — that method is
    /// not on the public surface, so the scanner uses reflection here to drive it without
    /// having to instantiate a design-time <c>IMigrator</c> or a database connection.
    /// </summary>
    private static void InvokeUp(Migration migration, MigrationBuilder builder)
    {
        var upMethod = migration.GetType().GetMethod(
            "Up",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(MigrationBuilder) },
            modifiers: null)
            ?? throw new InvalidOperationException(
                $"Migration type {migration.GetType().FullName} has no Up(MigrationBuilder) override to invoke.");

        upMethod.Invoke(migration, new object[] { builder });
    }

    /// <summary>
    /// A zero-side-effect <see cref="Storporate.Infrastructure.Authorization.IAccountContext"/>
    /// the model-walking helper above passes to <see cref="WriteDbContext"/>'s
    /// constructor. Mirrors the property-bag shape the unit tests already use
    /// (see <c>Storporate.Tests.Unit.Portfolio.Analysis.PortfolioAnalysisJobProcessorTests.TestAccountContext</c>).
    /// </summary>
    private sealed class StaticAccountContext : Storporate.Infrastructure.Authorization.IAccountContext
    {
        public Guid? UserId => null;
        public Guid? AccountId => null;
        public bool IsAdministrator => false;
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}