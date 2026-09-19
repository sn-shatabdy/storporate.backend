namespace Storporate.Modules.StudentGrowthExperience.Exceptions;

/// <summary>
/// Thrown by <c>CreateExplorationComparisonHandler</c> when either side of
/// the requested comparison has not yet produced an
/// <see cref="Storporate.SharedKernel.Entities.ExplorationSummaryVersion"/>.
/// The comparison prompt is built from the two summaries' gaps /
/// suggestions; without at least one summary version on each side there is
/// nothing to compare. Mapped to <c>409 exploration_has_no_summary</c> by
/// <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class ExplorationHasNoSummaryException : Exception
{
    public ExplorationHasNoSummaryException(Guid explorationId)
        : base($"Exploration '{explorationId}' has no summary yet — finish at least one turn before comparing.")
    {
    }
}
