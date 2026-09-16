namespace Storporate.Modules.Portfolio;

/// <summary>
/// Response DTO for <c>POST /api/portfolio/items</c>. Shape is identical to
/// <see cref="PortfolioItemResponse"/> (the create endpoint doesn't need anything extra,
/// and reusing the same record keeps the wire format consistent between create and
/// list for the same resource).
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
    DateTimeOffset CreatedAt)
{
    public static CreatePortfolioItemResponse FromEntity(Storporate.SharedKernel.Entities.PortfolioItem item) =>
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
            item.CreatedAt);
}