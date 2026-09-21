namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>
/// Thrown when a student applies to a posting they already applied to.
/// Mapped to <c>409 application_already_submitted</c> by <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class ApplicationAlreadySubmittedException : Exception
{
    public ApplicationAlreadySubmittedException()
        : base("You have already applied to this posting.")
    {
    }
}
