namespace Storporate.Modules.StudentGrowthExperience.Exceptions;

/// <summary>
/// Thrown by the retry handler when the target exploration is not in
/// <see cref="Storporate.SharedKernel.Entities.ExplorationStatuses.Failed"/>
/// — retrying a non-failed exploration would either double-enqueue against
/// an in-flight <c>AdvisorTurn</c> job, silently overwrite a successful
/// outcome, or be a no-op against an Idle exploration whose pending job is
/// still queued. Mapped to <c>409 exploration_not_retryable</c> by
/// <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class ExplorationNotRetryableException : Exception
{
    public ExplorationNotRetryableException(string currentStatus)
        : base($"Exploration cannot be retried from status '{currentStatus}'.")
    {
    }
}
