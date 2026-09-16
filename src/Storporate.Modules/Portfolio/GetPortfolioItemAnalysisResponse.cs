using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Wire-format response for <c>GET /api/portfolio/items/{id}/analysis</c>. Mirrors the
/// shape the STOR-38 Phase 5 frontend consumes: the per-item analysis status, the most
/// recent <c>Analyzed</c> timestamp (null until the worker reaches <see cref="PortfolioAnalysisStatuses.Analyzed"/>),
/// the latest failure error message (populated only when the item is in <see cref="PortfolioAnalysisStatuses.Failed"/>),
/// and the list of skill findings the worker wrote for the item — empty (never null) while the
/// analysis is still in <see cref="PortfolioAnalysisStatuses.NotAnalyzed"/> /
/// <see cref="PortfolioAnalysisStatuses.Analyzing"/> /
/// <see cref="PortfolioAnalysisStatuses.Failed"/> /
/// <see cref="PortfolioAnalysisStatuses.Unsupported"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate DTO rather than reusing <see cref="PortfolioItemResponse"/>.</b>
/// The create / list endpoints don't surface analysis state at all (it's a STOR-38 Phase 3
/// addition). Mixing the analysis shape into <see cref="PortfolioItemResponse"/> would force
/// two unrelated surface changes (those endpoints would gain nullable analysis fields
/// they have no use for) and entangle two independent response contracts behind one record.
/// </para>
/// <para>
/// <b>Why <see cref="Skills"/> is an array, never <see langword="null"/>.</b> The Phase 5
/// frontend's evidence-detail page renders "no skills yet" in the
/// <see cref="PortfolioAnalysisStatuses.NotAnalyzed"/> /
/// <see cref="PortfolioAnalysisStatuses.Analyzing"/> states — a null array there would
/// force a <c>?? &amp;[]</c> on every read site.
/// </para>
/// </remarks>
public sealed record GetPortfolioItemAnalysisResponse(
    string Status,
    DateTimeOffset? LastAnalyzedAt,
    string? ErrorMessage,
    IReadOnlyList<PortfolioSkillFindingResponse> Skills);

/// <summary>
/// Single skill finding within
/// <see cref="GetPortfolioItemAnalysisResponse"/>: the AI-derived skill name, the confidence band
/// (<see cref="ConfidenceBands"/>) assigned by the STOR-38 Phase 2 worker, and the model's
/// short evidence-grounded justification.
/// </summary>
public sealed record PortfolioSkillFindingResponse(
    string SkillName,
    string ConfidenceBand,
    string Explanation);
