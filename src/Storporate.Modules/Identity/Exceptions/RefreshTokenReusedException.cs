namespace Storporate.Modules.Identity.Exceptions;

/// <summary>
/// A refresh token that was already rotated out (its session's <c>ReplacedBySessionId</c> is
/// already set) was presented again — a signal of possible token theft. The handler that throws
/// this is responsible for revoking the entire session family before this exception propagates
/// (see the plan's Phase 3 <c>RefreshSessionHandler</c>). Mapped by <c>GlobalExceptionHandler</c>
/// to <c>401</c>.
/// </summary>
public sealed class RefreshTokenReusedException : Exception
{
    public RefreshTokenReusedException()
        : base("This session is no longer valid. Please log in again.")
    {
    }

    public RefreshTokenReusedException(string message)
        : base(message)
    {
    }
}
