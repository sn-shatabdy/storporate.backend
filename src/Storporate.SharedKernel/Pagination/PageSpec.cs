namespace Storporate.SharedKernel.Pagination;

/// <summary>
/// Normalized pagination parameters — the output of <see cref="PageRequest.ToSpec"/>,
/// consumed by <see cref="PaginationExtensions.ToPagedResultAsync{T}"/> and by
/// <see cref="SortMap{T}.TryApply"/>.
/// </summary>
/// <remarks>
/// <see cref="Skip"/> is the only <c>Skip/Take</c> primitive exposed to handlers, so
/// every <c>OFFSET</c> the system produces against EF Core flows through one clamped
/// arithmetic site. The <c>Math.Max(0, ...)</c> guard means a hostile <see cref="PageRequest.PageNumber"/>
/// can never produce a negative <see cref="Skip"/>, and the <c>int.MaxValue</c> clamp on
/// <see cref="PageNumber"/> keeps <c>(PageNumber - 1) * PageSize</c> from overflowing
/// even if a future caller somehow sets a giant page number before <see cref="PageRequest.ToSpec"/>
/// gets a chance to normalize it (defense-in-depth — <see cref="PageRequest.ToSpec"/> already
/// constrains <see cref="PageNumber"/> to a sane range).
/// </remarks>
public readonly record struct PageSpec(
    int PageNumber,
    int PageSize,
    string? SortBy,
    bool SortDescending)
{
    /// <summary>Row offset to pass to <c>Skip(...)</c> for <c>OFFSET</c>-style pagination.
    /// Clamped at <c>0</c> on the low side (no negative offsets) and at
    /// <c>int.MaxValue - PageSize</c> on the high side (no <c>int</c> overflow when the
    /// handler follows up with a <c>Take(PageSize)</c>).</summary>
    public int Skip
    {
        get
        {
            if (PageNumber < 1)
            {
                return 0;
            }

            // Use long arithmetic so a hypothetical pathological PageNumber * PageSize
            // cannot wrap to a negative int. The clamp on the high side ensures Skip +
            // Take always fits in an int, which EF Core's Skip/Take parameters require.
            var skip = (long)(PageNumber - 1) * PageSize;
            if (skip < 0)
            {
                return 0;
            }

            var maxSkip = (long)int.MaxValue - PageSize;
            return skip > maxSkip ? int.MaxValue - PageSize : (int)skip;
        }
    }
}
