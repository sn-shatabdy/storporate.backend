namespace Storporate.Modules.DiscoveryHiring.TalentSearch;

/// <summary>
/// Centralised limits for the talent-search pipeline. Constants (not an
/// options class) so the test hosts do not have to register placeholder
/// values for an empty <c>IOptions&lt;TalentSearchDefaults&gt;</c>; the
/// pipeline is intentionally fixed for now and these knobs would only be
/// exposed at runtime if a future story adds operator-tunable limits.
/// </summary>
public static class TalentSearchDefaults
{
    /// <summary>Number of nearest-neighbor candidates the
    /// <c>SearchTalentJobProcessor</c> pulls from the talent-index
    /// repository for the ranking LLM call.</summary>
    public const int CandidatePoolSize = 20;

    /// <summary>Hard cap on the number of results returned to the
    /// employer; the processor trims the ranked list (plus any unranked
    /// tail) to this number before writing
    /// <c>TalentSearchRequest.ResultJson</c>.</summary>
    public const int ResultLimit = 10;

    /// <summary>Minimum number of characters in a valid
    /// <c>POST</c> query. Matches the validator's
    /// <c>talent_search_query_too_short</c> error code.</summary>
    public const int MinQueryLength = 10;

    /// <summary>Maximum number of characters in a valid
    /// <c>POST</c> query. Matches the validator's
    /// <c>talent_search_query_too_long</c> error code AND the
    /// <c>TalentSearchRequests.QueryText</c> column length.</summary>
    public const int MaxQueryLength = 1000;

    /// <summary>Cap on the <c>reason</c> text the ranking LLM emits per
    /// candidate. Anything longer is truncated by
    /// <c>SearchTalentResponseParser</c>; the plan keeps reasons short
    /// so the FE never has to scroll.</summary>
    public const int MaxReasonLength = 400;

    /// <summary>Maximum number of characters the ranking prompt's
    /// candidate block can carry. Mirrors the advisor's per-prompt
    /// character cap. The processor truncates candidates (reducing items
    /// per candidate and skills per item) until the block fits.</summary>
    public const int PromptCandidateCharBudget = 24_000;

    /// <summary>Maximum number of skills the requirements LLM call may
    /// emit; surplus skills are dropped by
    /// <c>SearchTalentRequirementsParser</c>.</summary>
    public const int MaxExtractedSkills = 10;

    /// <summary>Cap on each extracted skill name's length.</summary>
    public const int MaxExtractedSkillLength = 60;

    /// <summary>Cap on the requirements LLM's one-sentence summary.</summary>
    public const int MaxRequirementsSummaryLength = 300;

    /// <summary>Cap on the text the prompt builder feeds the embedding
    /// model. Sits comfortably under the Bionic embedding model's
    /// 8K-token envelope (~ 4 chars per token for English).</summary>
    public const int EmbeddingInputMaxCharacters = 2_000;
}
