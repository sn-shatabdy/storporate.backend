namespace Storporate.Modules.Identity.Exceptions;

/// <summary>
/// The presented OTP code has reached its <c>MaxAttempts</c> of wrong guesses and is locked, even
/// though it may not yet be expired — the caller must request a new code. Mapped by
/// <c>GlobalExceptionHandler</c> to <c>401</c>.
/// </summary>
public sealed class OtpLockedException : Exception
{
    public OtpLockedException()
        : base("This code has been locked after too many incorrect attempts. Please request a new code.")
    {
    }

    public OtpLockedException(string message)
        : base(message)
    {
    }
}
