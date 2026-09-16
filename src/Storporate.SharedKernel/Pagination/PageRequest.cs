namespace Storporate.SharedKernel.Pagination;

/// <summary>
/// Query-string-bound pagination request. Plain C# record (no CQRS bus) — Storporate
/// minimal-API endpoints bind the request directly via <c>[AsParameters]</c> and pass it
/// straight into a handler <see langword="static"/> method, matching the pattern already
/// used throughout the Identity module (see <c>src/Storporate.Modules/Identity/IdentityEndpoints.cs</c>).
/// </summary>
/// <remarks>
/// Subclasses extend this record with the resource-specific filter fields
/// (<c>Action</c>, <c>FromDate</c>, etc.) and inherit the page/sort surface for free.
/// <see cref="ToSpec"/> normalizes the four pagination inputs into a single
/// <see cref="PageSpec"/> the handler then feeds into a <see cref="SortMap{T}"/> and
/// <c>ToPagedResultAsync</c> call.
/// </remarks>
public abstract record PageRequest
{
    /// <summary>Default page size when the caller omits <see cref="PageSize"/> or sends a
    /// value below <c>1</c>. Conservative enough to keep a single response body small
    /// (≈20 rows), generous enough that the common browse-the-list flow doesn't need
    /// paginated sub-fetches to be useful.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>Hard upper bound on <see cref="PageSize"/>. A caller asking for a million
    /// rows in one response gets clamped down to this many — keeps the response shape
    /// and the COUNT/SKIP/TAKE query cost bounded regardless of caller input.</summary>
    public const int MaxPageSize = 100;

    /// <summary>1-based page number. <see langword="null"/> or values below <c>1</c>
    /// normalize to <c>1</c> in <see cref="ToSpec"/>.</summary>
    public int? PageNumber { get; init; }

    /// <summary>Page size. <see langword="null"/> or values below <c>1</c> normalize to
    /// <see cref="DefaultPageSize"/>. Values above <see cref="MaxPageSize"/> clamp down to
    /// <see cref="MaxPageSize"/>. Together this gives every caller a working page size
    /// without any handler-level bounds-check on each query.</summary>
    public int? PageSize { get; init; }

    /// <summary>Optional sort key — a case-insensitive name the resource's
    /// <see cref="SortMap{T}"/> maps to a typed <c>OrderBy</c>/<c>OrderByDescending</c>
    /// expression. <see langword="null"/> falls through to the map's
    /// <see cref="SortMap{T}.DefaultKey"/>.</summary>
    public string? SortBy { get; init; }

    /// <summary>When <see langword="true"/>, the resolved sort key applies
    /// <c>OrderByDescending</c> instead of <c>OrderBy</c>. Only meaningful when
    /// <see cref="SortBy"/> is non-null and recognized by the handler's
    /// <see cref="SortMap{T}"/>.</summary>
    public bool SortDescending { get; init; }

    /// <summary>
    /// Normalizes the four pagination inputs into a single <see cref="PageSpec"/>. See
    /// <c>PageRequestTests</c> for the full normalization matrix (null/out-of-range
    /// inputs, default/missing sort key, descending toggle).
    /// </summary>
    public PageSpec ToSpec() =>
        new(
            PageNumber: PageNumber is null or < 1 ? 1 : PageNumber.Value,
            PageSize: PageSize switch
            {
                null or < 1 => DefaultPageSize,
                > MaxPageSize => MaxPageSize,
                _ => PageSize.Value,
            },
            SortBy: SortBy,
            SortDescending: SortDescending);
}
