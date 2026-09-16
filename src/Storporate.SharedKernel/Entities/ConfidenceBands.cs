namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The allowed values for <see cref="PortfolioSkillFinding.ConfidenceBand"/> — the three
/// qualitative bands the STOR-38 background worker assigns per skill per portfolio item.
/// Kept as string constants (not an enum), mirroring <see cref="JobStatus"/>,
/// <see cref="PortfolioAnalysisStatuses"/>, and <see cref="PortfolioCategories"/> so the
/// database column stays a readable string and adding a new band later doesn't require a
/// migration to widen a numeric range.
/// </summary>
/// <remarks>
/// <b>Bands vs. scores.</b> STOR-30 deliberately shipped a banded scale rather than a numeric
/// confidence score: bands are what a 12B-parameter local model can defend on evidence
/// text, and they're what the UI renders (green/amber/red + label) on both the portfolio
/// list and the per-item detail page. Numeric scores would invite false precision the model
/// doesn't have.
/// </remarks>
public static class ConfidenceBands
{
    /// <summary>The evidence clearly backs the skill — direct, unambiguous evidence in the
    /// submitted text. Renders as the success-green semantic color in the UI.</summary>
    public const string Strong = "Strong";

    /// <summary>The evidence partially backs the skill — relevant but incomplete. Renders as
    /// the warning-amber semantic color in the UI.</summary>
    public const string Developing = "Developing";

    /// <summary>The student named the skill but the evidence doesn't substantiate it (or the
    /// worker noticed the skill is missing entirely from the evidence). Renders as the
    /// critical-red semantic color in the UI.</summary>
    public const string Missing = "Missing";
}
