using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Response DTO for <c>POST /api/portfolio/items</c>. Shape is identical to
/// <see cref="PortfolioItemResponse"/> (the create endpoint doesn't need anything extra,
/// and reusing the same record keeps the wire format consistent between create and
/// list for the same resource). STOR-38 Phase 3 addendum: also exposes
/// <see cref="AnalysisStatus"/> and <see cref="LastAnalyzedAt"/> on the create response so
/// the wire shape stays in lock-step with the list endpoint — a freshly created item
/// always has <c>AnalysisStatus = "NotAnalyzed"</c> and <c>LastAnalyzedAt = null</c> at
/// this point, which is correct and expected.
/// </summary>
public sealed record CreatePortfolioItemResponse(
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
    bool ShareOriginalWithEmployers)
{
    public static CreatePortfolioItemResponse FromEntity(PortfolioItem item) =>
        new(
            item.Id,
            item.Label,
            item.Category,
            item.CustomCategoryText,
            item.SubmissionType,
            item.OriginalFileName,
            item.ContentType,
            item.FileSizeBytes,
            item.ExternalUrl,
            item.Description,
            item.CreatedAt,
            item.AnalysisStatus,
            item.LastAnalyzedAt,
            item.ShareOriginalWithEmployers);
}
