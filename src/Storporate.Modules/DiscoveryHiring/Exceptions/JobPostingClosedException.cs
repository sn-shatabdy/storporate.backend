namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>
/// Thrown when an Organization tries to edit or change the status of a Closed job posting.
/// Mapped to <c>409 job_posting_closed</c> by <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class JobPostingClosedException : Exception
{
    public JobPostingClosedException()
        : base("This posting is closed and can no longer be changed.")
    {
    }
}
