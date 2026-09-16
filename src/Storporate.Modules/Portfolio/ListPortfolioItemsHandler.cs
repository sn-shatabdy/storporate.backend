using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Pagination;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Builds a paginated <see cref="PagedResult{PortfolioItemResponse}"/> over
/// <see cref="PortfolioItem"/> rows for the
/// <c>GET /api/portfolio/items</c> endpoint. The query is naturally scoped
/// to the caller's account by the EF Core global query filter installed in
/// <see cref="WriteDbContext.OnModelCreating"/> for every
/// <see cref="IAccountScoped"/> entity, so the handler does not need to add a
/// redundant <c>WHERE AccountId = ...</c> term (matches how
/// <see cref="Storporate.Modules.SecurityGovernance.ListAuditLogEntriesHandler"/>
/// relies on the explicit AccountId filter it carries because
/// <see cref="AuditLogEntry"/> is intentionally <em>not</em>
/// <see cref="IAccountScoped"/>, the only entity that needs that pattern).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>.</b> The
/// endpoint never mutates rows — every caller-visible side effect is the JSON
/// response — so EF Core's change tracker is pure overhead for the read. Same
/// reasoning as <see cref="Storporate.Modules.SecurityGovernance.ListAuditLogEntriesHandler"/>.
/// </para>
/// <para>
/// <b>Why no explicit AccountId filter.</b>
/// <see cref="PortfolioItem"/> implements <see cref="IAccountScoped"/> — the same
/// three-layer isolation pipeline that <see cref="Job"/> runs through (STOR-62) — so
/// the global query filter restricts reads to the ambient account automatically.
/// Adding a manual <c>e =&gt; e.AccountId == accountContext.AccountId</c> predicate
/// would be redundant noise on top of a security-critical filter that is already
/// running (and that a future refactor can re-verify with a single integration test).
/// </para>
/// <para>
/// <b>Why a local <see cref="UnknownSortKeyException"/> definition.</b> STOR-62's
/// architecture test enforces a no-sibling-module-dependency rule: a module cannot
/// reach across into another module's namespace. The SecurityGovernance module owns
/// the canonical <c>UnknownSortKeyException</c> today, so we either define our own
/// parallel one (kept semantically identical) or break the architecture gate.
/// Reusing the named class verbatim is impossible without a sibling-module
/// reference, so the Portfolio module ships its own copy.
/// </para>
/// </remarks>
public static class ListPortfolioItemsHandler
{
    /// <summary>
    /// The whitelist of supported <c>sortBy</c> values for this handler.
    /// <c>createdAt</c> is both the default and the tie-breaker, so an unsorted
    /// call and a <c>sortBy=createdAt</c> call produce identical, stable orderings.
    /// The <c>label</c> map is exposed for a future "alphabetical browse" mode the
    /// student page may want.
    /// </summary>
    private static readonly SortMap<PortfolioItem> SortMap = new SortMap<PortfolioItem>()
        // createdAt is the most common sort — newest-first chronological browsing — so it
        // doubles as the default. TieBreakBy(createdAt) means even callers who omit
        // sortBy get a stable, gap-free ordering across pages.
        .Register("createdAt", item => item.CreatedAt)
        .Default("createdAt")
        .Map("label", item => item.Label)
        .Map("category", item => item.Category)
        .TieBreakBy(item => item.Id);

    public static async Task<PagedResult<PortfolioItemResponse>> ExecuteAsync(
        ListPortfolioItemsRequest request,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);

        var spec = request.ToSpec();

        var query = dbContext.PortfolioItems.AsNoTracking();

        var ordered = SortMap.TryApply(query, spec);
        if (ordered is null)
        {
            throw new UnknownSortKeyException(spec.SortBy!, SortMap.Keys);
        }

        // Count before projecting: CountAsync against the DTO projection becomes
        // SELECT COUNT(*) FROM (SELECT ... FROM ... WHERE ...) which on a student's
        // many-row portfolio feed is more expensive than the same COUNT against the
        // entity source. Counting the entity source first produces a plain
        // SELECT COUNT(*) FROM ... WHERE ... — the same query EF Core would have
        // issued anyway, just without the projection wrapper.
        var totalCount = await ordered.CountAsync(cancellationToken).ConfigureAwait(false);

        if (totalCount == 0)
        {
            return new PagedResult<PortfolioItemResponse>(
                Items: Array.Empty<PortfolioItemResponse>(),
                PageNumber: spec.PageNumber,
                PageSize: spec.PageSize,
                TotalCount: 0);
        }

        // StorageKey never leaves the DB, mirroring AuditLogEntryResponse omitting
        // Hash / PreviousHash. The student-facing UI does not need the key — only
        // the server-side delete handler reaches into IArtifactStore.
        var items = await ordered
            .Select(item => new PortfolioItemResponse(
                item.Id,
                item.Label,
                item.Category,
                item.CustomCategoryText,
                item.SubmissionType,
                item.OriginalFileName,
                item.ContentType,
                item.FileSizeBytes,
                item.ExternalUrl,
                item.Description,
                item.CreatedAt))
            .Skip(spec.Skip)
            .Take(spec.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<PortfolioItemResponse>(
            Items: items,
            PageNumber: spec.PageNumber,
            PageSize: spec.PageSize,
            TotalCount: totalCount);
    }
}

/// <summary>
/// Surfaces an unsupported <c>sortBy</c> value as a 400 <c>unknown_sort_key</c>
/// error rather than a 500. Semantic twin of
/// <see cref="Storporate.Modules.SecurityGovernance.UnknownSortKeyException"/>;
/// defined here to keep the Portfolio module's dependency graph inside its
/// own namespace (the architecture test forbids sibling-module references).
/// The class shape / property set / message format mirrors the SecurityGovernance
/// one verbatim so the GlobalExceptionHandler's switch arm treats both the same
/// way (it pattern-matches on a literal <c>UnknownSortKeyException</c> type
/// name, so a future cleanup story should add a shared base type rather than
/// collapsing these two concretely).
/// </summary>
public sealed class UnknownSortKeyException : Exception
{
    public UnknownSortKeyException(string requestedSortKey, IReadOnlyList<string> supportedKeys)
        : base($"Unsupported sort key '{requestedSortKey}'. Supported keys: {string.Join(", ", supportedKeys)}.")
    {
        RequestedSortKey = requestedSortKey;
        SupportedKeys = supportedKeys;
    }

    public string RequestedSortKey { get; }

    public IReadOnlyList<string> SupportedKeys { get; }
}