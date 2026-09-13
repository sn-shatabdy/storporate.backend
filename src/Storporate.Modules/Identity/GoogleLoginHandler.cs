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
    private static readonly string[] ValidActorTypes =
    [
        ActorTypes.Student,
        ActorTypes.Organization,
        ActorTypes.University,
        ActorTypes.Club,
    ];

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
