using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Security;

namespace Storporate.Infrastructure.Security;

/// <summary>
/// <see cref="IGoogleIdTokenValidator"/> backed by <c>Google.Apis.Auth</c>'s
/// <see cref="GoogleJsonWebSignature.ValidateAsync(string, ValidationSettings, CancellationToken)"/>,
/// which fetches and caches Google's JSON Web Key set, verifies the RS256 signature, and checks
/// the standard <c>iss</c>/<c>exp</c>/<c>aud</c> claims. The audience is locked to our own
/// <see cref="GoogleAuthOptions.ClientId"/> so a token issued for any other Google Cloud project
/// is rejected — the backend never blindly trusts a claim from the frontend.
///
/// On failure the validator throws <see cref="GoogleAuthenticationException"/> (a
/// <c>SharedKernel</c> type) rather than <c>Storporate.Modules.Identity.Exceptions.GoogleLoginFailedException</c>
/// because <c>Storporate.Infrastructure</c> must not reference
/// <c>Storporate.Modules</c>. <c>GoogleLoginHandler</c> catches and rethrows the public-facing
/// type, which <c>GlobalExceptionHandler</c> maps to <c>401</c>.
/// </summary>
public sealed class GoogleIdTokenValidator(
    IOptions<GoogleAuthOptions> options) : IGoogleIdTokenValidator
{
    public async Task<GoogleIdentityResult> ValidateAsync(
        string idToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            throw new GoogleAuthenticationException();
        }

        var clientId = options.Value.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            // Startup-time validation already rejects this; the throw here is a belt-and-braces
            // guard so a misconfigured environment can never accidentally accept any audience.
            throw new GoogleAuthenticationException("Google sign-in is not configured on this server.");
        }

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { clientId },
                });

            if (string.IsNullOrWhiteSpace(payload.Subject) || string.IsNullOrWhiteSpace(payload.Email))
            {
                throw new GoogleAuthenticationException();
            }

            return new GoogleIdentityResult(
                GoogleSubjectId: payload.Subject,
                Email: payload.Email,
                EmailVerified: payload.EmailVerified);
        }
        catch (GoogleAuthenticationException)
        {
            throw;
        }
        catch (InvalidJwtException)
        {
            // Signature mismatch, wrong audience, expired, or otherwise structurally invalid.
            throw new GoogleAuthenticationException();
        }
        catch (Exception)
        {
            // Catch-all so a Google API outage surfaces as a clean auth failure rather than an
            // unhandled 500 — see GlobalExceptionHandler for the 401 mapping.
            throw new GoogleAuthenticationException();
        }
    }
}
