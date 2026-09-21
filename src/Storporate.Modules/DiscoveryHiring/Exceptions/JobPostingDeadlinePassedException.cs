namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>
/// Thrown when a student tries to apply to a posting whose application deadline is in the past.
/// Mapped to <c>409 job_posting_deadline_passed</c> by <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class JobPostingDeadlinePassedException : Exception
{
    public JobPostingDeadlinePassedException()
        : base("The application deadline for this posting has passed.")
    {
    }
}
