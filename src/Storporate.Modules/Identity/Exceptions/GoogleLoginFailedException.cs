namespace Storporate.Modules.Identity.Exceptions;

/// <summary>
/// The Google ID token forwarded to <c>POST /api/auth/google</c> failed validation (bad
/// signature, wrong audience, expired, missing <c>sub</c>/<c>email</c>, etc.). Thrown by
/// <c>GoogleIdTokenValidator</c>; the public message is intentionally generic so the caller
/// cannot use it as an oracle. Mapped by <c>GlobalExceptionHandler</c> to <c>401</c>.
/// </summary>
public sealed class GoogleLoginFailedException : Exception
{
    public GoogleLoginFailedException()
        : base("Google sign-in failed. Please try again.")
    {
    }

    public GoogleLoginFailedException(string message)
        : base(message)
    {
    }
}
