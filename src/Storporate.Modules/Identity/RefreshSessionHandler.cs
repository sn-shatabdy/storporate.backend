using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity.Exceptions;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Modules.Identity;

/// <summary>
/// Validates a presented refresh token, rotates the matching session, and detects reuse (theft)
/// — re-submitting an already-rotated-out refresh token is a strong signal of token theft, so
/// the entire <c>FamilyId</c> is revoked in one shot, killing the rotated-in successor too.
///
/// Order of checks matters (see <c>RefreshSessionReusedException</c>):
///   1. Token hashes to a known session row.
///   2. Session is not revoked / not expired.
///   3. Session has not been rotated out — if <c>ReplacedBySessionId</c> is set, this is reuse:
///      revoke every session in <c>FamilyId</c>, then throw.
///   4. Issue a new token pair whose session belongs to the same <c>FamilyId</c>; the old
///      session's <c>ReplacedBySessionId</c> is wired to the new one.
/// </summary>
public static class RefreshSessionHandler
{
    public static async Task<AuthTokenResult> ExecuteAsync(
        string refreshToken,
        string? userAgent,
        WriteDbContext dbContext,
        IJwtTokenService tokenService,
        CancellationToken cancellationToken)
    {
        var hashedRefreshToken = Sha256CodeHasher.Hash(refreshToken);
        var now = DateTime.UtcNow;

        var session = await dbContext.Sessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.HashedRefreshToken == hashedRefreshToken, cancellationToken);

        // Step 1: no session row matches the presented token — generic invalid (do not leak
        // whether the token was ever issued).
        if (session is null)
        {
            throw new RefreshTokenInvalidException();
        }

        // Step 2: already revoked or expired — distinct from reuse below; this is just a stale
        // or already-logged-out token, not a theft signal.
        if (session.RevokedAt is not null)
        {
            throw new RefreshTokenInvalidException();
        }

        if (session.ExpiresAt <= now)
        {
            throw new RefreshTokenInvalidException();
        }

        // Step 3: already rotated out — reuse / theft. Revoke the entire family so the
        // attacker (or whoever holds the successor token) loses access too.
        if (session.ReplacedBySessionId is not null)
        {
            await RevokeEntireFamilyAsync(dbContext, session.FamilyId, now, cancellationToken);
            throw new RefreshTokenReusedException();
        }

        if (session.User is null)
        {
            // Navigation property not loaded despite the Include — guard so we never mint
            // tokens against a user nobody can resolve.
            throw new RefreshTokenInvalidException();
        }

        // Step 4: rotate. IssueRotatedTokensAsync atomically claims this session for rotation
        // (sets ReplacedBySessionId) and inserts a new Session row in the same FamilyId.
        // If two concurrent refresh calls both reach this line with the same still-valid
        // token, only one wins the atomic UPDATE — the other sees the claim as already taken
        // (a raced loser, indistinguishable from a knowing token-reuse attacker) and must be
        // treated identically: revoke the entire family, then throw.
        var tokens = await tokenService.IssueRotatedTokensAsync(
            session.User,
            userAgent,
            session.FamilyId,
            session.Id,
            cancellationToken);

        if (tokens is null)
        {
            await RevokeEntireFamilyAsync(dbContext, session.FamilyId, now, cancellationToken);
            throw new RefreshTokenReusedException();
        }

        return tokens;
    }

    /// <summary>Revokes every non-revoked session in <paramref name="familyId"/>, stamping
    /// <c>RevokedAt</c> so subsequent refresh attempts hit the generic invalid branch (and the
    /// access token still inside its short window is killed at the next request via
    /// /me/sessions introspection — see the plan's Phase 3 spec).</summary>
    private static async Task RevokeEntireFamilyAsync(
        WriteDbContext dbContext,
        Guid familyId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var familySessions = await dbContext.Sessions
            .Where(s => s.FamilyId == familyId && s.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var familySession in familySessions)
        {
            familySession.RevokedAt = now;
            familySession.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
