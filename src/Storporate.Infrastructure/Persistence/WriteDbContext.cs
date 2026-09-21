using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence.Configurations;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence;

/// <summary>
/// The primary write-side EF Core context. Named "WriteDbContext" (rather than a generic
/// "AppDbContext") to leave room for a dedicated read-side context later without a rename.
/// </summary>
/// <remarks>
/// <para>
/// STOR-62 Phase 4: the constructor takes <see cref="IAccountContext"/> as a singleton
/// dependency so the global query filter installed in <see cref="OnModelCreating"/> can
/// reference <c>this._accountContext</c> in a per-entity filter lambda. EF Core
/// special-cases query filters that reference the owning DbContext instance's own
/// members and re-binds them to the actual running instance at query time, regardless
/// of which instance's <see cref="OnModelCreating"/> originally built the cached model.
/// That means the filter is always evaluated against the current request's ambient
/// account, even though the compiled model is cached for the life of the process.
/// </para>
/// <para>
/// The same captured reference is also used by the
/// <c>RowLevelSecurityInterceptor</c> registered against this context in
/// <c>Program.cs</c>; the interceptor runs the save-time tenancy guard against
/// <see cref="IAccountScoped.AccountId"/> mismatches.
/// </para>
/// </remarks>
public sealed class WriteDbContext : DbContext
{
    private readonly IAccountContext _accountContext;

    public WriteDbContext(DbContextOptions<WriteDbContext> options, IAccountContext accountContext)
        : base(options)
    {
        _accountContext = accountContext;
    }

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<User> Users => Set<User>();

    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    public DbSet<Session> Sessions => Set<Session>();

    // STOR-37 Phase 1: student-facing portfolio capture. Implements IAccountScoped, so
    // the global query filter installed below scopes every read to the caller's account
    // automatically; STOR-62's three-layer isolation pipeline covers PortfolioItem rows
    // with no per-entity extra work.
    public DbSet<PortfolioItem> PortfolioItems => Set<PortfolioItem>();

    // STOR-38 Phase 1: AI-derived per-skill findings written by the Phase 2 background
    // worker and read by the Phase 3 per-item analysis endpoint. Implements IAccountScoped
    // (and so inherits the same global query filter as PortfolioItem above) — the Phase 2
    // worker copies the parent item's AccountId onto every finding it inserts so the
    // filter resolves against the same indexed column.
    public DbSet<PortfolioSkillFinding> PortfolioSkillFindings => Set<PortfolioSkillFinding>();

    // STOR-63 Phase 1: tamper-evident audit log. Rows are written via raw Npgsql by
    // AuditLogWriter (not via this DbContext's change tracker), but reads through the
    // Administrator query endpoint in Phase 3 use this DbSet for AsNoTracking LINQ queries.
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    // STOR-40 Phase 1: student-growth advisor and feed (six tenant tables + one
    // global table). Every IAccountScoped entity below is picked up by the global
    // query filter installed in OnModelCreating and by the RLS policies added in
    // the AddStudentGrowthRowLevelSecurity migration. The global FeedItems table
    // is intentionally NOT IAccountScoped — it is read by every student and written
    // only by the feed-refresh background service.
    public DbSet<Exploration> Explorations => Set<Exploration>();
    public DbSet<ExplorationMessage> ExplorationMessages => Set<ExplorationMessage>();
    public DbSet<ExplorationSummaryVersion> ExplorationSummaryVersions => Set<ExplorationSummaryVersion>();
    public DbSet<StudentContextNote> StudentContextNotes => Set<StudentContextNote>();
    public DbSet<ExplorationComparison> ExplorationComparisons => Set<ExplorationComparison>();
    public DbSet<StudentFeedEntry> StudentFeedEntries => Set<StudentFeedEntry>();
    public DbSet<FeedItem> FeedItems => Set<FeedItem>();

    // STOR-43 Phase 1: talent-search student opt-in (tenant) + non-tenant search
    // index. StudentSearchProfile is IAccountScoped so the global query filter
    // restricts reads to the caller's account automatically; the matching
    // account_scoped RLS policy is installed by the AddTalentSearchTables
    // migration. TalentIndexEntry is intentionally NOT IAccountScoped — every
    // Organization account reads from it via the Phase 2 search endpoint, and
    // only the RefreshTalentIndexEntryProcessor background job writes to it.
    public DbSet<StudentSearchProfile> StudentSearchProfiles => Set<StudentSearchProfile>();
    public DbSet<TalentIndexEntry> TalentIndexEntries => Set<TalentIndexEntry>();

    // STOR-43 Phase 2: the Organization's plain-language search request. IAccountScoped
    // so the global query filter + save-time RowLevelSecurityInterceptor + the
    // account_scoped RLS policy installed by the AddTalentSearchRequestRowLevelSecurity
    // migration all key on its AccountId — a cross-account id never matches the
    // busy-check lookup on POST, nor the GET endpoint's id lookup.
    public DbSet<TalentSearchRequest> TalentSearchRequests => Set<TalentSearchRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // All entity mappings live as standalone IEntityTypeConfiguration classes
        // (see Persistence/Configurations/), applied explicitly here.
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new OtpCodeConfiguration());
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new JobConfiguration());
        modelBuilder.ApplyConfiguration(new PortfolioItemConfiguration());
        modelBuilder.ApplyConfiguration(new PortfolioSkillFindingConfiguration());
        modelBuilder.ApplyConfiguration(new AuditLogEntryConfiguration());
        modelBuilder.ApplyConfiguration(new ExplorationConfiguration());
        modelBuilder.ApplyConfiguration(new ExplorationMessageConfiguration());
        modelBuilder.ApplyConfiguration(new ExplorationSummaryVersionConfiguration());
        modelBuilder.ApplyConfiguration(new StudentContextNoteConfiguration());
        modelBuilder.ApplyConfiguration(new ExplorationComparisonConfiguration());
        modelBuilder.ApplyConfiguration(new StudentFeedEntryConfiguration());
        modelBuilder.ApplyConfiguration(new FeedItemConfiguration());
        modelBuilder.ApplyConfiguration(new StudentSearchProfileConfiguration());
        modelBuilder.ApplyConfiguration(new TalentIndexEntryConfiguration());
        modelBuilder.ApplyConfiguration(new TalentSearchRequestConfiguration());

        // Global query filter for every IAccountScoped entity type. We walk the model
        // once via reflection to discover which CLR types implement IAccountScoped,
        // then invoke the strongly-typed generic instance helper
        // ApplyAccountScopedFilter<TEntity> for each one. The helper writes the filter
        // as a literal C# lambda whose body references `this._accountContext`, so the
        // C# compiler emits a field access against the owning DbContext instance.
        // EF Core's instance-substitution for DbContext-member query filters then
        // re-binds those member accesses to the actually-running WriteDbContext
        // instance at query time — independent of model caching and independent of
        // whatever DI lifetime IAccountContext ends up using.
        ApplyAccountScopedQueryFilters(modelBuilder);
    }

    private void ApplyAccountScopedQueryFilters(ModelBuilder modelBuilder)
    {
        var applyFilterMethod = typeof(WriteDbContext)
            .GetMethod(nameof(ApplyAccountScopedFilter), BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ApplyAccountScopedFilter<TEntity> helper not found.");

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (clrType is null || !typeof(IAccountScoped).IsAssignableFrom(clrType))
            {
                continue;
            }

            // `this` is the key: ApplyAccountScopedFilter<TEntity> is an instance method
            // on WriteDbContext, so invoking it through `this` makes its body's
            // `_accountContext` references resolve against this very instance, not
            // against whichever instance first triggered model build.
            var closedMethod = applyFilterMethod.MakeGenericMethod(clrType);
            closedMethod.Invoke(this, new object[] { modelBuilder });
        }
    }

    private void ApplyAccountScopedFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, IAccountScoped
    {
        // Literal C# lambda — the compiler emits a closure over `this` and a field
        // access `this._accountContext.AccountId` / `this._accountContext.IsAdministrator`.
        // EF Core recognizes that pattern and substitutes the actual running DbContext
        // instance's _accountContext at query time, so the filter always reflects the
        // current request's ambient account even though OnModelCreating (and the
        // compiled model it produces) is cached forever after the first call.
        Expression<Func<TEntity, bool>> filter = e =>
            e.AccountId == _accountContext.AccountId || _accountContext.IsAdministrator;

        modelBuilder.Entity<TEntity>().HasQueryFilter(filter);
    }
}
