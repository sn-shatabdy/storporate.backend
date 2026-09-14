using Storporate.SharedKernel.Entities;

namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// Validates a Google ID token forwarded from the frontend after the NextAuth Google provider
/// completes the browser-side OAuth handshake, returning the claims needed to get-or-create the
/// matching <see cref="User"/>. Concrete implementation
/// (<c>Storporate.Infrastructure.Security.GoogleIdTokenValidator</c>) lives in
/// <c>Storporate.Infrastructure</c>; callers in modules/API code depend only on this interface.
/// </summary>
public interface IGoogleIdTokenValidator
{
    /// <summary>
    /// Validates <paramref name="idToken"/> against the Google JSON Web Key set, verifying its
    /// signature, issuer, expiry, and audience against the configured <c>ClientId</c>.
    /// </summary>
    /// <exception cref="Modules.Identity.Exceptions.GoogleLoginFailedException">
    /// Thrown when the token is rejected (bad signature, wrong audience, expired, etc.). The
    /// message never reveals which check failed in detail to the caller, to avoid oracle hints.
    /// </exception>
    Task<GoogleIdentityResult> ValidateAsync(string idToken, CancellationToken cancellationToken = default);
}

/// <summary>The claims extracted from a successfully-validated Google ID token.</summary>
public sealed record GoogleIdentityResult(
    string GoogleSubjectId,
    string Email,
    bool EmailVerified);
