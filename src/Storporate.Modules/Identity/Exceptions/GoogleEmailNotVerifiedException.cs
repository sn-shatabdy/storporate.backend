namespace Storporate.Modules.Identity.Exceptions;

/// <summary>
/// A Google sign-in attempt presented an ID token whose <c>email_verified</c> claim is
/// <c>false</c> for an email that already exists in Storporate. Auto-linking that Google
/// identity to the existing OTP-verified account would be an account-takeover vector —
/// Google has not actually confirmed that the caller controls this email address, only
/// that the email was claimed during some Google-linked sign-up elsewhere. Mapped by
/// <c>GlobalExceptionHandler</c> to <c>409</c> (a state conflict: the resource exists
/// but the presented credentials cannot prove ownership, so the caller must use the
/// already-established email-OTP path to log in).
/// </summary>
public sealed class GoogleEmailNotVerifiedException : Exception
{
    public GoogleEmailNotVerifiedException()
        : base("This email is already registered. Please sign in using your email code instead.")
    {
    }

    public GoogleEmailNotVerifiedException(string message)
        : base(message)
    {
    }
}
