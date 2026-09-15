using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity.Exceptions;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Modules.Identity;

/// <summary>
/// Validates a Google ID token, then gets-or-creates the <see cref="User"/> it refers to and
/// mints a token pair via <see cref="IssueSessionHandler"/>. Mirrors <see cref="VerifyOtpHandler"/>'s
/// "ActorType required for new accounts only" rule — a returning user may omit <c>ActorType</c>.
/// Lookups prefer Google's stable <c>sub</c> claim, falling back to email only when no row has
/// that <c>sub</c> set yet (covers accounts created purely via email-OTP that later link Google).
/// </summary>
public static class GoogleLoginHandler
{
    /// <summary>
    /// Actor types a brand-new user is allowed to register as via <c>/api/auth/google</c>.
    /// Deliberately excludes <see cref="ActorTypes.Administrator"/> — Administrators are
    /// platform-staff accounts that bypass workspace isolation (see the STOR-62 plan) and
    /// must be provisioned out-of-band, never self-registered through a public auth flow.
    /// Exposed publicly so a regression test can lock down the invariant
    /// (<c>GoogleLoginHandlerTests.ValidActorTypes_DoesNotIncludeAdministrator</c>); a future
    /// auth path that forgets to filter Administrator would otherwise pass code review and
    /// silently grant an attacker the bypass role.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidActorTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ActorTypes.Student,
            ActorTypes.Organization,
            ActorTypes.University,
            ActorTypes.Club,
        };

    public static async Task<GoogleLoginResult> ExecuteAsync(
        string idToken,
        string? actorType,
        string? userAgent,
        WriteDbContext dbContext,
        IGoogleIdTokenValidator googleIdTokenValidator,
        IJwtTokenService tokenService,
        CancellationToken cancellationToken)
    {
        GoogleIdentityResult googleIdentity;
        try
        {
            googleIdentity = await googleIdTokenValidator.ValidateAsync(idToken, cancellationToken);
        }
        catch (GoogleAuthenticationException exception)
        {
            // Validator lives in Infrastructure and can't reference Modules exceptions —
            // wrap as the public-facing type here so GlobalExceptionHandler can map it.
            throw new GoogleLoginFailedException(exception.Message);
        }

        var normalizedEmail = RequestOtpHandler.NormalizeEmail(googleIdentity.Email);
        var now = DateTime.UtcNow;

        // Prefer subject-id lookup so an email change at Google doesn't silently bind the new
        // address to an existing account. Fall back to email only when no row claims this sub
        // yet (covers accounts created via email-OTP that are linking Google for the first time).
        var user = await dbContext.Users.FirstOrDefaultAsync(
            u => u.GoogleSubjectId == googleIdentity.GoogleSubjectId,
            cancellationToken);

        var isNewUser = user is null;
        if (user is null)
        {
            user = await dbContext.Users.FirstOrDefaultAsync(
                u => u.Email == normalizedEmail,
                cancellationToken);

            if (user is null)
            {
                if (string.IsNullOrWhiteSpace(actorType))
                {
                    throw new ActorTypeRequiredException();
                }

                if (!ValidActorTypes.Contains(actorType))
                {
                    throw new ActorTypeRequiredException($"'{actorType}' is not a recognized actor type.");
                }

                user = new User
                {
                    Id = Guid.NewGuid(),
                    Email = normalizedEmail,
                    ActorType = actorType,
                    GoogleSubjectId = googleIdentity.GoogleSubjectId,
                    VerificationStatus = actorType == ActorTypes.Student
                        ? VerificationStatuses.Verified
                        : VerificationStatuses.Unverified,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                dbContext.Users.Add(user);
            }
            else
            {
                // Returning email-OTP-only account is linking Google for the first time.
                // Security: only auto-link when Google has actually verified this email — the
                // OTP path has already established ownership, so for a new Google sign-in to
                // attach itself to that account, Google itself must confirm the caller controls
                // this email. Without that, this code would happily bind an attacker's Google
                // identity to a victim account whose email the attacker happened to claim on
                // some other Google-linked service.
                if (!googleIdentity.EmailVerified)
                {
                    throw new GoogleEmailNotVerifiedException();
                }

                user.GoogleSubjectId = googleIdentity.GoogleSubjectId;
                user.UpdatedAt = now;
                // Found via email-fallback — not a brand-new user.
                isNewUser = false;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var tokens = await IssueSessionHandler.ExecuteAsync(user, userAgent, tokenService, cancellationToken);

        return new GoogleLoginResult(user, isNewUser, tokens);
    }
}
