namespace Storporate.Modules.StudentGrowthExperience.Exceptions;

/// <summary>
/// Thrown by <c>CreateExplorationHandler</c> when the caller already owns
/// <c>AdvisorOptions.MaxExplorationsPerStudent</c> explorations. The cap
/// keeps the per-student open-conversation count bounded — students are
/// expected to keep a handful of live directions in flight and refresh /
/// prune the rest. Mapped to <c>409 exploration_limit_reached</c> by
/// <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class ExplorationLimitReachedException : Exception
{
    public ExplorationLimitReachedException(int limit)
        : base($"You have reached the maximum of {limit} explorations. Delete one before starting a new one.")
    {
    }
}
