using System.Linq.Expressions;

namespace Storporate.SharedKernel.Pagination;

/// <summary>
/// A small, type-safe whitelist of sort keys a paginated query supports. Each registered
/// key maps to a single typed <c>OrderBy</c>/<c>OrderByDescending</c> expression; any
/// unknown key makes <see cref="TryApply"/> return <see langword="null"/> instead of
/// silently falling back (or throwing), so the endpoint can translate the failure into a
/// <c>400 Bad Request</c> shaped exactly like the rest of the API's error responses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an explicit whitelist, not just whatever <c>OrderBy</c> string the client
/// sent.</b> Translating an arbitrary string into a LINQ <c>OrderBy</c> against an
/// arbitrary entity is unsafe by construction — there is no compile-time check that
/// the column exists, no way to defend against column-name injection, and no
/// guarantee the eventual <c>SQL</c> is even well-formed. The whitelist forces the
/// developer to enumerate every legitimate sort column once, at registration time,
/// and rejects everything else at the boundary.
/// </para>
/// <para>
/// <b>Why <c>Expression&lt;Func&lt;T, object&gt;&gt;</c> rather than
/// <c>Expression&lt;Func&lt;T, TKey&gt;&gt;</c>.</b> EF Core's <c>IOrderedQueryable&lt;T&gt;.ThenBy/ThenByDescending</c>
/// overloads accept <c>Expression&lt;Func&lt;T, object&gt;&gt;</c> directly, so callers
/// don't have to pin a particular <c>TKey</c> at registration time. The runtime cost
/// is one extra <c>Convert</c> node inside the expression tree, which EF Core's
/// translator strips during SQL generation.
/// </para>
/// <para>
/// <b>Why a mandatory tie-breaker.</b> Without a stable, deterministic secondary sort,
/// pagination against equal primary-sort rows can show the same row twice or skip a
/// row between pages (Postgres doesn't guarantee row order without an explicit
/// <c>ORDER BY</c>). <see cref="TieBreakBy"/> enforces the convention: <c>TryApply</c>
/// throws if no tie-breaker has been registered, so a developer can't accidentally
/// ship an unstable <c>OrderBy</c>.
/// </para>
/// </remarks>
public sealed class SortMap<T>
{
    /// <summary>One registered sort key: its case-insensitive string name and the typed
    /// expression that orders by it.</summary>
    private readonly record struct Entry(string Key, Expression<Func<T, object>> Selector);

    private readonly List<Entry> _entries = [];
    private string? _defaultKey;
    private Expression<Func<T, object>>? _tieBreaker;

    /// <summary>
    /// Registers a sortable key. The <paramref name="key"/> is matched
    /// case-insensitively against the inbound <see cref="PageSpec.SortBy"/>; the
    /// <paramref name="selector"/> is the typed expression the resulting
    /// <c>OrderBy</c>/<c>OrderByDescending</c> runs against.
    /// </summary>
    /// <remarks>Duplicate keys (case-insensitive) throw at registration time so a typo
    /// fails the build, not a runtime query.</remarks>
    public SortMap<T> Register(string key, Expression<Func<T, object>> selector)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(selector);

        if (TryFindEntry(key) is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate sort key '{key}' on {typeof(SortMap<T>).FullName} — sort keys must be unique.");
        }

        _entries.Add(new Entry(key, selector));
        return this;
    }

    /// <summary>
    /// Convenience for the most common case: register the key alongside its selector in
    /// a single call. Equivalent to <c>Register(key, selector)</c>.
    /// </summary>
    public SortMap<T> Map(string key, Expression<Func<T, object>> selector) => Register(key, selector);

    /// <summary>
    /// Marks the supplied <paramref name="key"/> as the fallback used when
    /// <see cref="PageSpec.SortBy"/> is <see langword="null"/> or unrecognized by the
    /// caller. The key must already have been registered (<see cref="Register"/>), or
    /// this method throws. There can only be one default per <see cref="SortMap{T}"/>;
    /// calling it twice replaces the previous default.
    /// </summary>
    public SortMap<T> Default(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (TryFindEntry(key) is null)
        {
            throw new InvalidOperationException(
                $"Default key '{key}' on {typeof(SortMap<T>).FullName} is not registered — call Register('{key}', ...) first.");
        }

        _defaultKey = key;
        return this;
    }

    /// <summary>The currently-registered default key, or <see langword="null"/> if no
    /// <see cref="Default(string)"/> call has been made.
    /// Used by <see cref="TryApply"/> to resolve a null/missing
    /// <see cref="PageSpec.SortBy"/>.</summary>
    public string? DefaultKey => _defaultKey;

    /// <summary>
    /// Sets the mandatory secondary sort applied via <c>ThenBy</c> after every primary
    /// sort. Throws on <see cref="TryApply"/> if this hasn't been called yet — the
    /// point of <c>TieBreakBy</c> is that an unstable pagination is a bug, not a
    /// stylistic choice.
    /// </summary>
    public SortMap<T> TieBreakBy(Expression<Func<T, object>> tieBreaker)
    {
        ArgumentNullException.ThrowIfNull(tieBreaker);
        _tieBreaker = tieBreaker;
        return this;
    }

    /// <summary>
    /// The case-preserved names of every registered sort key, in registration order.
    /// Exposed so callers (e.g. <c>UnknownSortKeyException</c>) can echo the full
    /// supported-key list back to the caller without maintaining a parallel copy.
    /// </summary>
    public IReadOnlyList<string> Keys => _entries.ConvertAll(e => e.Key);

    /// <summary>
    /// Applies the requested sort to <paramref name="query"/> based on
    /// <paramref name="spec"/>. Returns <see langword="null"/> if the supplied
    /// <see cref="PageSpec.SortBy"/> is non-null and doesn't match any registered key —
    /// letting the caller surface a validation error rather than silently substituting a
    /// different order.
    /// </summary>
    /// <remarks>
    /// Resolution order:
    /// <list type="number">
    ///   <item>If <see cref="PageSpec.SortBy"/> is non-null and registered (case-insensitive),
    ///   apply the matching <c>OrderBy</c>/<c>OrderByDescending</c> by its expression.</item>
    ///   <item>If <see cref="PageSpec.SortBy"/> is <see langword="null"/> or empty,
    ///   fall back to the <see cref="DefaultKey"/>.</item>
    ///   <item>If <see cref="PageSpec.SortBy"/> is non-null and not registered, return
    ///   <see langword="null"/> — caller surfaces a <c>400 Bad Request</c>.</item>
    /// </list>
    /// After the primary sort is applied, <see cref="TieBreakBy"/>'s expression is
    /// always <c>ThenBy</c>-ed onto the result, regardless of which primary path was
    /// taken — guaranteeing stable pagination across all calls.
    /// </remarks>
    public IOrderedQueryable<T>? TryApply(IQueryable<T> query, PageSpec spec)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (_tieBreaker is null)
        {
            throw new InvalidOperationException(
                $"SortMap<{typeof(T).Name}> has no TieBreakBy — pagination without a stable "
                + "tie-breaker can return duplicate or skipped rows across pages. "
                + "Call .TieBreakBy(...) before .TryApply(...).");
        }

        var requested = spec.SortBy;
        Entry? matched = null;

        if (!string.IsNullOrEmpty(requested))
        {
            matched = TryFindEntry(requested);
        }
        else if (_defaultKey is not null)
        {
            matched = TryFindEntry(_defaultKey);
        }

        if (matched is null)
        {
            // No requested key and no default registered: apply only the tie-breaker as
            // a stable, deterministic order.
            if (!string.IsNullOrEmpty(requested))
            {
                // Non-null + unrecognized is the explicit validation-error path — the caller
                // turns the null return into a 400-shaped error response. A null SortBy is
                // not this case; it falls through to the tie-breaker-only ordering.
                return null;
            }

            return query.OrderBy(_tieBreaker);
        }

        var entry = matched.Value;
        return spec.SortDescending
            ? query.OrderByDescending(entry.Selector).ThenBy(_tieBreaker)
            : query.OrderBy(entry.Selector).ThenBy(_tieBreaker);
    }

    private Entry? TryFindEntry(string key)
    {
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
