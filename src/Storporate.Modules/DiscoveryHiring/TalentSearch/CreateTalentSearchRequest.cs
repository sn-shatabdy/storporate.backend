namespace Storporate.Modules.DiscoveryHiring.TalentSearch;

/// <summary>
/// The JSON body for <c>POST /api/discovery/talent-searches</c>. The
/// validator enforces the two plan-mandated invariants: the query is
/// trimmed, must be at least
/// <see cref="TalentSearchDefaults.MinQueryLength"/> characters
/// (<c>talent_search_query_too_short</c>), and at most
/// <see cref="TalentSearchDefaults.MaxQueryLength"/>
/// (<c>talent_search_query_too_long</c>).
/// </summary>
public sealed class CreateTalentSearchRequest
{
    /// <summary>The employer's plain-language description of who they
    /// need. Trimmed by the validator before the length check so leading
    /// / trailing whitespace does not count against the cap.</summary>
    public string? Query { get; init; }
}
