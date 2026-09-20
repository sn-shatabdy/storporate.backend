namespace Storporate.Modules.StudentGrowthExperience.Requests;

/// <summary>Request body for <c>POST /api/growth/explorations</c>.
/// The optional <see cref="Direction"/> is the student's opening message
/// for the new exploration — it becomes the first
/// <see cref="Storporate.SharedKernel.Entities.ExplorationMessage"/> with
/// <c>Role=Student</c> before the opening-turn job is enqueued.</summary>
public sealed class CreateExplorationRequest
{
    /// <summary>The student's opening direction. Optional — an exploration
    /// may be created without an initial message (the advisor will then
    /// ask the first question in the opening turn). Capped at 4 000
    /// characters by <see cref="Validators.CreateExplorationValidator"/>.</summary>
    public string? Direction { get; set; }
}
