using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Response DTO for a single <see cref="PortfolioItem"/> as surfaced through the
/// <c>POST /api/portfolio/items</c> and
/// <c>GET /api/portfolio/items</c> endpoints. Mirrors the entity
/// one-for-one <em>except</em> for <see cref="PortfolioItem.StorageKey"/>, which never
/// leaves the server-side boundary — the storage key is an implementation detail of the
/// <c>IArtifactStore</c> contract, not something a student-facing UI needs to display
/// or pass back. STOR-38 Phase 3 addendum: also exposes the per-item
/// <see cref="AnalysisStatus"/> and <see cref="LastAnalyzedAt"/> so the Phase 5 portfolio
/// list page can render its status badge without a per-row API call.
/// </summary>
/// <remarks>
/// Same rationale as <c>AuditLogEntryResponse</c> omitting the chain-hash fields:
/// exposing <see cref="PortfolioItem.StorageKey"/> would let any caller with read
/// access construct a <c>GetAsync(storageKey)</c> against the <c>IArtifactStore</c>
/// URL pattern (or a future pre-signed-URL endpoint) for a blob they don't have
/// permission to view. Keeping the key internal avoids that second-layer leak.
/// </remarks>
public sealed record PortfolioItemResponse(
    Guid Id,
    string Label,
    string Category,
    string? CustomCategoryText,
    string SubmissionType,
    string? OriginalFileName,
    string? ContentType,
    long? FileSizeBytes,
    string? ExternalUrl,
    string? Description,
    DateTimeOffset CreatedAt,
    string AnalysisStatus,
    DateTimeOffset? LastAnalyzedAt,
    IReadOnlyList<PortfolioSkillPreview> Skills);

/// <summary>
/// Condensed one-line preview of a single AI-derived skill finding for the timeline
/// view on <c>/dashboard/portfolio</c>: the skill name and the
/// <see cref="Storporate.SharedKernel.Entities.ConfidenceBands"/> value the STOR-38
/// worker assigned. Mirrors the per-finding
/// <see cref="PortfolioSkillFindingResponse"/> on the detail page minus the
/// <c>Explanation</c> field — the timeline badge carries only what the UI needs to
/// label + color a chip, and the full evidence-grounded justification stays
/// scoped to the detail endpoint where the student audits the model's reasoning.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sibling record rather than reusing <see cref="PortfolioSkillFindingResponse"/>.</b>
/// The detail-page response is the full evidence record; the timeline preview is the
/// two-field condensation. Sharing a record would let future drift on either surface
/// (a renamed explanation field, a new optional sub-score) silently leak onto the other.
/// </para>
/// <para>
/// <b>Why co-located with <see cref="PortfolioItemResponse"/> rather than in its own file.</b>
/// The other condensed response in this module
/// (<see cref="PortfolioSkillFindingResponse"/>) is co-located with the response that
/// uses it (<see cref="GetPortfolioItemAnalysisResponse"/>) — same one-DTO-per-file
/// shape, just two records side-by-side in one file when one record is the
/// component-of-another.
/// </para>
/// </remarks>
public sealed record PortfolioSkillPreview(string SkillName, string ConfidenceBand);
