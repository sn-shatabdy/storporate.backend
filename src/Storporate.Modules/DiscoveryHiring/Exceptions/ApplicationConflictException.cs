namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>
/// Thrown when a save against <see cref="Storporate.SharedKernel.Entities.JobApplication"/>
/// loses a concurrency race — the row was updated by someone else between read and write.
/// Mapped to <c>409 application_conflict</c> by <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class ApplicationConflictException : Exception
{
    public ApplicationConflictException()
        : base("Someone else changed this application. Reload and try again.")
    {
    }
}
