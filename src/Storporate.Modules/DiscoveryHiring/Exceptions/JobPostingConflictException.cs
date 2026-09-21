namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>
/// Thrown when a save against <see cref="Storporate.SharedKernel.Entities.JobPosting"/>
/// loses a concurrency race — the row was updated by someone else between read and write.
/// Mapped to <c>409 job_posting_conflict</c> by <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class JobPostingConflictException : Exception
{
    public JobPostingConflictException()
        : base("Someone else changed this posting. Reload and try again.")
    {
    }
}
