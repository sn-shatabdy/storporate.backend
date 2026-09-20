namespace Storporate.Modules.StudentGrowthExperience.Requests;

/// <summary>Request body for <c>PUT /api/growth/explorations/{id}/title</c>.
/// 1-200 characters after trim — enforced by
/// <see cref="Validators.UpdateExplorationTitleValidator"/>.</summary>
public sealed class UpdateExplorationTitleRequest
{
    public string? Title { get; set; }
}
