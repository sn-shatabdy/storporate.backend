namespace Storporate.Modules.StudentGrowthExperience.Responses;

/// <summary>The full detail shape returned by <c>GET /api/growth/explorations/{id}</c>.
/// Includes the metadata, the messages in chronological order, and the
/// latest summary version (when one exists).</summary>
public sealed record ExplorationDetailResponse(
    Guid Id,
    string Title,
    string Status,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExplorationMessageResponse> Messages,
    ExplorationSummaryResponse? LatestSummary);
