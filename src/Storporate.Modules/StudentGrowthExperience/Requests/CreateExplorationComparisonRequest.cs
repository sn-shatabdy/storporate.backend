namespace Storporate.Modules.StudentGrowthExperience.Requests;

/// <summary>Request body for <c>POST /api/growth/explorations/compare</c>.
/// Two exploration ids to compare side-by-side. The handler rejects the
/// request with <c>400 compare_needs_two</c> when both ids resolve to the
/// same row.</summary>
public sealed class CreateExplorationComparisonRequest
{
    public Guid FirstExplorationId { get; set; }
    public Guid SecondExplorationId { get; set; }
}
