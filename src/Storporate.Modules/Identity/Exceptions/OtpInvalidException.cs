namespace Storporate.Modules.Identity.Exceptions;

/// <summary>
/// The code presented to <c>POST /api/auth/otp/verify</c> does not match the hashed code on file
/// for the email (and the code is not yet locked or expired — see
/// <see cref="OtpLockedException"/>). Mapped by <c>GlobalExceptionHandler</c> to <c>400</c>.
/// </summary>
public sealed class OtpInvalidException : Exception
{
    public OtpInvalidException()
        : base("The code you entered is incorrect.")
    {
    }

    public OtpInvalidException(string message)
        : base(message)
    {
    }
}
