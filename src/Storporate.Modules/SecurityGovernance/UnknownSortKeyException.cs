namespace Storporate.Modules.SecurityGovernance;

/// <summary>
/// The paginated query handler received a <c>sortBy</c> value that isn't registered in
/// its <see cref="Storporate.SharedKernel.Pagination.SortMap{T}"/>. Thrown by
/// <see cref="ListAuditLogEntriesHandler"/> when a caller sends an unknown sort key
/// — the handler deliberately does not silently fall back to a default order, because
/// a silent fallback would let a typo (e.g. <c>createdat</c> instead of <c>createdAt</c>)
/// mask a bug from the UI side.
/// </summary>
/// <remarks>
/// Mapped by <c>GlobalExceptionHandler</c> to <c>400 Bad Request</c> with the
/// <c>unknown_sort_key</c> error code — matching the codebase's two-field
/// <c>ErrorResponse</c> convention so every 4xx response the API surfaces looks the same
/// to a client.
/// </remarks>
public sealed class UnknownSortKeyException : Exception
{
    public UnknownSortKeyException(string requestedKey, IEnumerable<string> supportedKeys)
        : base($"Unknown sort key '{requestedKey}'. Supported keys: {string.Join(", ", supportedKeys)}.")
    {
        RequestedKey = requestedKey;
        SupportedKeys = supportedKeys.ToArray();
    }

    /// <summary>The unrecognized sort key the caller supplied (verbatim, including
    /// casing — matches <see cref="Storporate.SharedKernel.Pagination.PageSpec.SortBy"/>).</summary>
    public string RequestedKey { get; }

    /// <summary>The full set of registered sort keys for the handler, in registration
    /// order — useful to surface in the error message so the caller can self-correct.</summary>
    public IReadOnlyList<string> SupportedKeys { get; }
}
