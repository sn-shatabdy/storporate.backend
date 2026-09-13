namespace Storporate.SharedKernel.Security;

/// <summary>
/// Thrown by <c>GoogleIdTokenValidator</c> when a Google ID token fails validation (bad
/// signature, wrong audience, expired, missing <c>sub</c>/<c>email</c>, etc.). Lives in
/// <c>SharedKernel</c> (rather than <c>Storporate.Modules.Identity.Exceptions</c> alongside the
/// other auth exceptions) precisely because <c>Storporate.Infrastructure</c> needs to throw it
/// and <c>Infrastructure</c> must not reference <c>Storporate.Modules</c> — keeping a circular
/// project reference out. The <see cref="Storporate.Modules.Identity.Exceptions.GoogleLoginFailedException"/>
/// thrown by <c>GoogleLoginHandler</c> is the user-facing equivalent; the handler swallows this
/// internal type and rethrows the public-facing one. Mapped to <c>401</c> by
/// <c>GlobalExceptionHandler</c> only when wrapped as <c>GoogleLoginFailedException</c>.
/// </summary>
public sealed class GoogleAuthenticationException : Exception
{
    public GoogleAuthenticationException()
        : base("Google ID token failed validation.")
    {
    }

    public GoogleAuthenticationException(string message)
        : base(message)
    {
    }
}
