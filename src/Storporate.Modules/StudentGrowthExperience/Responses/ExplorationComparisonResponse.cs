namespace Storporate.Modules.StudentGrowthExperience.Responses;

/// <summary>Response body for <c>GET /api/growth/explorations/compare/{id}</c>.
/// <see cref="ResultText"/> is non-null only when <see cref="Status"/> is
/// <see cref="Storporate.SharedKernel.Entities.ExplorationComparisonStatuses.Completed"/>.</summary>
public sealed record ExplorationComparisonResponse(
    Guid Id,
    Guid FirstExplorationId,
    Guid SecondExplorationId,
    string Status,
    string? ResultText,
    DateTimeOffset CreatedAt);
