namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>
/// Thrown when a <c>q</c> filter on a job-postings or jobs endpoint is longer than 100 characters.
/// Mapped to <c>400 job_posting_query_invalid</c> by <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class JobPostingQueryInvalidException : Exception
{
    public JobPostingQueryInvalidException()
        : base("Search text must be 100 characters or fewer.")
    {
    }
}
