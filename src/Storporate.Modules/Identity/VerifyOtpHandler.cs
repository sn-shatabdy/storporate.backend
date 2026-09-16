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
/// Validates a presented OTP code and, on success, gets-or-creates the <see cref="User"/> for the
/// email before minting a token pair via <see cref="IssueSessionHandler"/>. Attempt/lock/expiry
/// checks run in a specific order so the acceptance behavior holds exactly: a code that has
/// already reached <see cref="OtpCode.MaxAttempts"/> is reported as locked (<see
/// cref="OtpLockedException"/>) even when the *next* attempt presented would otherwise have been
/// correct — i.e. 5 wrong guesses lock the code, and a 6th attempt (right or wrong) always fails.
/// </summary>
/// <remarks>
/// Every failure branch writes its audit row
/// <em>before</em> throwing — the writer's swallow-and-log safety net only covers Postgres
/// failures, not the case where the audit write itself never ran. By placing the
/// <see cref="IAuditLogWriter.WriteAsync"/> call on the line immediately above each throw we
/// guarantee the row exists whenever the handler ran far enough to reach that branch,
/// regardless of what happens to the exception afterwards. The success branch audits
/// <c>"login_succeeded"</c> against the resulting <see cref="User.Id"/> (not the email), so the
/// row keys to a stable, account-scoped resource id.
/// </remarks>
public static class VerifyOtpHandler
{
    /// <summary>
    /// Actor types a brand-new user is allowed to register as via <c>/api/auth/otp/verify</c>.
    /// Shared with <see cref="GoogleLoginHandler.ValidActorTypes"/> via
    /// <see cref="IdentityAllowedActorTypes.Set"/> so a future auth path can't drift and let
    /// <see cref="ActorTypes.Administrator"/> through.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidActorTypes = IdentityAllowedActorTypes.Set;

    public static async Task<VerifyOtpResult> ExecuteAsync(
        string email,
        string code,
        string? actorType,
        string? userAgent,
        WriteDbContext dbContext,
        IJwtTokenService tokenService,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = RequestOtpHandler.NormalizeEmail(email);
        var now = DateTime.UtcNow;

        var otpCode = await dbContext.OtpCodes
            .Where(o => o.Email == normalizedEmail && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (otpCode is null)
        {
            await auditLogWriter.WriteAsync(
                action: "otp_verify_failed",
                resourceType: "User",
                resourceId: normalizedEmail,
                metadataJson: AuditReasons.OtpNoPendingCode,
                cancellationToken: cancellationToken);
            throw new OtpInvalidException("No pending code was found for this email. Request a new code.");
        }

        // Checked before expiry so an already-locked code is reported as locked even if it also
        // happens to be expired by now — locking takes priority (see the plan's Phase 2 spec).
        if (otpCode.AttemptCount >= otpCode.MaxAttempts)
        {
            await auditLogWriter.WriteAsync(
                action: "otp_locked",
                resourceType: "User",
                resourceId: normalizedEmail,
                cancellationToken: cancellationToken);
            throw new OtpLockedException();
        }

        if (otpCode.ExpiresAt <= now)
        {
            await auditLogWriter.WriteAsync(
                action: "otp_verify_failed",
                resourceType: "User",
                resourceId: normalizedEmail,
                metadataJson: AuditReasons.OtpExpired,
                cancellationToken: cancellationToken);
            throw new OtpInvalidException("This code has expired. Request a new code.");
        }

        var hashedCode = Sha256CodeHasher.Hash(code);
        if (hashedCode != otpCode.HashedCode)
        {
            otpCode.AttemptCount++;
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditLogWriter.WriteAsync(
                action: "otp_verify_failed",
                resourceType: "User",
                resourceId: normalizedEmail,
                metadataJson: AuditReasons.OtpWrongCode,
                cancellationToken: cancellationToken);
            throw new OtpInvalidException();
        }

        otpCode.ConsumedAt = now;

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
        var isNewUser = user is null;

        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(actorType))
            {
                await auditLogWriter.WriteAsync(
                    action: "otp_verify_failed",
                    resourceType: "User",
                    resourceId: normalizedEmail,
                    metadataJson: AuditReasons.ActorTypeMissing,
                    cancellationToken: cancellationToken);
                throw new ActorTypeRequiredException();
            }

            if (!ValidActorTypes.Contains(actorType))
            {
                await auditLogWriter.WriteAsync(
                    action: "otp_verify_failed",
                    resourceType: "User",
                    resourceId: normalizedEmail,
                    metadataJson: AuditReasons.ActorTypeUnrecognized,
                    cancellationToken: cancellationToken);
                throw new ActorTypeRequiredException($"'{actorType}' is not a recognized actor type.");
            }

            user = new User
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                ActorType = actorType,
                VerificationStatus = actorType == ActorTypes.Student
                    ? VerificationStatuses.Verified
                    : VerificationStatuses.Unverified,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.Users.Add(user);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var tokens = await IssueSessionHandler.ExecuteAsync(user, userAgent, tokenService, cancellationToken);

        await auditLogWriter.WriteAsync(
            action: "login_succeeded",
            resourceType: "User",
            resourceId: user.Id.ToString(),
            cancellationToken: cancellationToken);

        return new VerifyOtpResult(user, isNewUser, tokens);
    }
}
