namespace Storporate.Modules.Identity.Exceptions;

/// <summary>
/// A registration attempt conflicts with an email that already has an account (for example, an
/// actor-type mismatch on an existing account, or a signup flow that requires a not-yet-registered
/// email). Mapped by <c>GlobalExceptionHandler</c> to <c>409</c>.
/// </summary>
public sealed class EmailAlreadyRegisteredException : Exception
{
    public EmailAlreadyRegisteredException()
        : base("An account with this email already exists.")
    {
    }

    public EmailAlreadyRegisteredException(string message)
        : base(message)
    {
    }
}
