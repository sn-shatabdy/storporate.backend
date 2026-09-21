namespace Storporate.Modules.DiscoveryHiring.JobPostings;

/// <summary>
/// The browse handler received a <c>sort</c> value that isn't <c>fit</c> or <c>newest</c>.
/// Mapped to <c>400 unknown_sort_key</c> by <c>GlobalExceptionHandler</c>. Lives in this
/// module rather than <see cref="Storporate.Modules.SecurityGovernance"/> so the
/// DiscoveryHiring module keeps its vertical-slice isolation (no sibling-module
/// dependency).
/// </summary>
public sealed class UnknownSortKeyException : Exception
{
    public UnknownSortKeyException(string requestedKey, IEnumerable<string> supportedKeys)
        : base($"Unknown sort key '{requestedKey}'. Supported keys: {string.Join(", ", supportedKeys)}.")
    {
        RequestedKey = requestedKey;
        SupportedKeys = supportedKeys.ToArray();
    }

    public string RequestedKey { get; }

    public IReadOnlyList<string> SupportedKeys { get; }
}
