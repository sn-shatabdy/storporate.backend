namespace Storporate.Modules.StudentGrowthExperience.Responses;

/// <summary>One row in the <c>GET /api/growth/explorations</c> response.
/// Ordered by <c>UpdatedAt</c> descending (newest first).</summary>
public sealed record ExplorationListItemResponse(
    Guid Id,
    string Title,
    string Status,
    DateTimeOffset UpdatedAt,
    int? LatestVersionNumber);
