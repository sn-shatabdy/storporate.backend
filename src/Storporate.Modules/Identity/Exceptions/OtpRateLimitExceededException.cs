namespace Storporate.Modules.Identity.Exceptions;

/// <summary>
/// Too many OTP requests were made for an email or IP within the configured window (see the
/// plan's Phase 2 <c>OtpRateLimitOptions</c>). Mapped by <c>GlobalExceptionHandler</c> to
/// <c>429</c>.
/// </summary>
public sealed class OtpRateLimitExceededException : Exception
{
    public OtpRateLimitExceededException()
        : base("Too many code requests. Please wait a moment before trying again.")
    {
    }

    public OtpRateLimitExceededException(string message)
        : base(message)
    {
    }
}
