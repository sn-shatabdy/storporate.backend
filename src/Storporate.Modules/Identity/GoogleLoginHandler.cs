using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity.Exceptions;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Auditing;
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
/// <remarks>
/// Mirrors <see cref="VerifyOtpHandler"/>'s "write before throw"
/// discipline for failure branches (<c>google_login_failed</c>,
/// <c>google_login_rejected_unverified_email</c>) and audits the success branch against the
/// resulting <see cref="User.Id"/>. Google branch exceptions do not carry an email through the
/// public path (the validator result lives in a local variable that isn't exposed) so
/// <c>ResourceId</c> is <see langword="null"/> for the failure rows — the email is captured
/// only on the success path where we have a confirmed user record.
/// </remarks>
public static class GoogleLoginHandler
{
    /// <summary>
    /// Actor types a brand-new user is allowed to register as via <c>/api/auth/google</c>.
    /// Shared with <see cref="VerifyOtpHandler.ValidActorTypes"/> via
    /// <see cref="IdentityAllowedActorTypes.Set"/> so a future auth path can't drift and let
    /// <see cref="ActorTypes.Administrator"/> through.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidActorTypes = IdentityAllowedActorTypes.Set;

    public static async Task<GoogleLoginResult> ExecuteAsync(
        string idToken,
        string? actorType,
        string? userAgent,
        WriteDbContext dbContext,
        IGoogleIdTokenValidator googleIdTokenValidator,
        IJwtTokenService tokenService,
        IAuditLogWriter auditLogWriter,
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
            // Audit the failed-login attempt before throwing so the row exists regardless
            // of how the exception propagates upstream.
            await auditLogWriter.WriteAsync(
                action: "google_login_failed",
                resourceType: "User",
                resourceId: null,
                cancellationToken: cancellationToken);
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
                    await auditLogWriter.WriteAsync(
                        action: "google_login_failed",
                        resourceType: "User",
                        resourceId: null,
                        metadataJson: AuditReasons.ActorTypeMissing,
                        cancellationToken: cancellationToken);
                    throw new ActorTypeRequiredException();
                }

                if (!ValidActorTypes.Contains(actorType))
                {
                    await auditLogWriter.WriteAsync(
                        action: "google_login_failed",
                        resourceType: "User",
                        resourceId: null,
                        metadataJson: AuditReasons.ActorTypeUnrecognized,
                        cancellationToken: cancellationToken);
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
                    await auditLogWriter.WriteAsync(
                        action: "google_login_rejected_unverified_email",
                        resourceType: "User",
                        resourceId: null,
                        cancellationToken: cancellationToken);
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

        await auditLogWriter.WriteAsync(
            action: "google_login_succeeded",
            resourceType: "User",
            resourceId: user.Id.ToString(),
            cancellationToken: cancellationToken);

        return new GoogleLoginResult(user, isNewUser, tokens);
    }
}
