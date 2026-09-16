namespace Storporate.SharedKernel.Pagination;

/// <summary>
/// Paginated query response: the requested page's items plus enough metadata for a
/// pagination bar to render (<see cref="TotalCount"/>, <see cref="TotalPages"/>,
/// <see cref="HasPrevious"/>, <see cref="HasNext"/>). Returned directly by
/// <see cref="PaginationExtensions.ToPagedResultAsync{T}"/>.
/// </summary>
/// <remarks>
/// Sealed record (no inheritance) so every consumer agrees on the exact wire shape;
/// adding a field is a breaking change to this type's API and the plan deliberately
/// keeps the surface minimal — there is no <c>Link</c> header or
/// <c>continuationToken</c> here, just the Skip/Take pagination metadata the
/// existing UI can consume directly.
/// </remarks>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount)
{
    /// <summary>Total number of pages given the current <see cref="PageSize"/>. <c>0</c>
    /// when there are no rows (avoids a divide-by-zero on the client). Otherwise
    /// <c>ceil(TotalCount / PageSize)</c>.</summary>
    public int TotalPages => PageSize <= 0 || TotalCount <= 0
        ? 0
        : (TotalCount + PageSize - 1) / PageSize;

    /// <summary><see langword="true"/> when there is at least one page before
    /// <see cref="PageNumber"/>.</summary>
    public bool HasPrevious => PageNumber > 1;

    /// <summary><see langword="true"/> when there is at least one page after
    /// <see cref="PageNumber"/>. False at the boundary or when the result set is empty.</summary>
    public bool HasNext => TotalPages > 0 && PageNumber < TotalPages;
}
