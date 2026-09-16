using Microsoft.EntityFrameworkCore;

namespace Storporate.SharedKernel.Pagination;

/// <summary>
/// Bridges a <see cref="PageSpec"/>-normalized, already-ordered <see cref="IQueryable{T}"/>
/// to a <see cref="PagedResult{T}"/> in one call: <c>COUNT(*)</c> for the metadata +
/// <c>Skip/Skip + Take</c> for the page's items. EF Core translates both to a single
/// batch of SQL statements per call.
/// </summary>
/// <remarks>
/// <para>
/// The extension accepts <see cref="IQueryable{T}"/> (not
/// <see cref="IOrderedQueryable{T}"/>) so callers can apply a <c>Select(projection)</c>
/// before pagination without losing the type-system convenience of a single chained
/// call. The compile-time "must be ordered" guarantee is enforced upstream by the
/// convention that every handler resolves its ordering through
/// <see cref="SortMap{T}.TryApply"/>, which mandates a tie-breaker and rejects
/// unregistered keys.
/// </para>
/// <para>
/// <b>Why two queries, not one.</b> Some EF Core providers can collapse
/// <c>COUNT</c> + page-fetch into a single round-trip via window functions
/// (<c>COUNT(*) OVER ()</c>), but doing so requires <c>Select(projection)</c> instead of
/// <c>ToListAsync()</c> on the entity set, which would change the result type from
/// <c>T</c> to a tuple/DTO and force every caller to re-project. Two clean roundtrips
/// (one <c>COUNT</c>, one <c>SELECT ... OFFSET ... LIMIT</c>) is the simpler shape and
/// keeps the <c>Skip/Take</c> side portable across every supported provider.
/// </para>
/// </remarks>
public static class PaginationExtensions
{
    /// <summary>
    /// Executes the supplied ordered query against EF Core, wrapping the result and the
    /// computed total row count into a <see cref="PagedResult{T}"/>. The query must
    /// already have a stable <c>OrderBy</c>/<c>OrderByDescending</c> chain applied
    /// (with a tie-breaker — see <see cref="SortMap{T}.TieBreakBy"/>); the extension does
    /// no further sorting of its own.
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> ordered,
        PageSpec spec,
        CancellationToken cancellationToken = default)
    {
        // CountAsync runs against the same query (sans ordering) so the COUNT is the row
        // count after every WHERE filter the caller applied, not the table total. EF Core
        // strips the ordering for the COUNT query automatically.
        var totalCount = await ordered.CountAsync(cancellationToken).ConfigureAwait(false);

        // Defensive: if there are no rows, skip the SELECT entirely — the materialized
        // Items list is empty either way, and Postgres' OFFSET against an empty set
        // would do nothing useful but would still cost a planner roundtrip.
        if (totalCount == 0)
        {
            return new PagedResult<T>(
                Items: Array.Empty<T>(),
                PageNumber: spec.PageNumber,
                PageSize: spec.PageSize,
                TotalCount: 0);
        }

        var items = await ordered
            .Skip(spec.Skip)
            .Take(spec.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<T>(
            Items: items,
            PageNumber: spec.PageNumber,
            PageSize: spec.PageSize,
            TotalCount: totalCount);
    }
}
