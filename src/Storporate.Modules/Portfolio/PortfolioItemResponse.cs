using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Response DTO for a single <see cref="PortfolioItem"/> as surfaced through the
/// <c>POST /api/portfolio/items</c> and
/// <c>GET /api/portfolio/items</c> endpoints. Mirrors the entity
/// one-for-one <em>except</em> for <see cref="PortfolioItem.StorageKey"/>, which never
/// leaves the server-side boundary — the storage key is an implementation detail of the
/// <c>IArtifactStore</c> contract, not something a student-facing UI needs to display
/// or pass back.
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
    DateTimeOffset CreatedAt);