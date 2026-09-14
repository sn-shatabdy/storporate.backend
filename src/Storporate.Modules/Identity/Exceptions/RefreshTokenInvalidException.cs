namespace Storporate.Modules.Identity.Exceptions;

/// <summary>
/// The refresh token presented to <c>POST /api/auth/refresh</c> does not match any active
/// session row, refers to a session whose <c>RevokedAt</c> is set, or refers to a session whose
/// <c>ExpiresAt</c> is in the past. Distinct from <see cref="RefreshTokenReusedException"/>,
/// which is thrown when the token <em>does</em> match a session row that has already been
/// rotated out (a much more serious signal of possible theft). Mapped by
/// <c>GlobalExceptionHandler</c> to <c>401</c>.
/// </summary>
public sealed class RefreshTokenInvalidException : Exception
{
    public RefreshTokenInvalidException()
        : base("This refresh token is not valid. Please log in again.")
    {
    }

    public RefreshTokenInvalidException(string message)
        : base(message)
    {
    }
}
