namespace Storporate.Modules.StudentGrowthExperience.Exceptions;

/// <summary>
/// Thrown by the add-message / refresh / retry handlers when the target
/// <see cref="Storporate.SharedKernel.Entities.Exploration"/> is currently
/// in <see cref="Storporate.SharedKernel.Entities.ExplorationStatuses.Working"/>
/// — an <c>AdvisorTurn</c> job has been claimed and is in flight, so a
/// second concurrent turn would race the in-flight one. Mapped to
/// <c>409 exploration_busy</c> by <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class ExplorationBusyException : Exception
{
    public ExplorationBusyException(Guid explorationId)
        : base($"Exploration '{explorationId}' is currently being processed.")
    {
    }
}
