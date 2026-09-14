using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity.Exceptions;
using Storporate.SharedKernel.Abstractions;
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
public static class VerifyOtpHandler
{
    private static readonly string[] ValidActorTypes =
    [
        ActorTypes.Student,
        ActorTypes.Organization,
        ActorTypes.University,
        ActorTypes.Club,
    ];

    public static async Task<VerifyOtpResult> ExecuteAsync(
        string email,
        string code,
        string? actorType,
        string? userAgent,
        WriteDbContext dbContext,
        IJwtTokenService tokenService,
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
            throw new OtpInvalidException("No pending code was found for this email. Request a new code.");
        }

        // Checked before expiry so an already-locked code is reported as locked even if it also
        // happens to be expired by now — locking takes priority (see the plan's Phase 2 spec).
        if (otpCode.AttemptCount >= otpCode.MaxAttempts)
        {
            throw new OtpLockedException();
        }

        if (otpCode.ExpiresAt <= now)
        {
            throw new OtpInvalidException("This code has expired. Request a new code.");
        }

        var hashedCode = Sha256CodeHasher.Hash(code);
        if (hashedCode != otpCode.HashedCode)
        {
            otpCode.AttemptCount++;
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new OtpInvalidException();
        }

        otpCode.ConsumedAt = now;

        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
        var isNewUser = user is null;

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

        return new VerifyOtpResult(user, isNewUser, tokens);
    }
}
