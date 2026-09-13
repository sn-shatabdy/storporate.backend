namespace Storporate.Modules.Identity.Exceptions;

/// <summary>
/// The email presented to <c>POST /api/auth/otp/verify</c> has no existing account, and the
/// request omitted (or supplied an unrecognized) <c>ActorType</c> — required to create a new
/// account, since actor type cannot be inferred or defaulted (see the plan's Phase 2
/// <c>VerifyOtpHandler</c>). Mapped by <c>GlobalExceptionHandler</c> to <c>400</c>.
/// </summary>
public sealed class ActorTypeRequiredException : Exception
{
    public ActorTypeRequiredException()
        : base("An actor type is required to create a new account.")
    {
    }

    public ActorTypeRequiredException(string message)
        : base(message)
    {
    }
}
