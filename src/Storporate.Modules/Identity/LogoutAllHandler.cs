using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;

namespace Storporate.Modules.Identity;

/// <summary>
/// Revokes every non-revoked <see cref="Storporate.SharedKernel.Entities.Session"/> belonging
/// to the caller (identified by the <c>sub</c> claim of their access token) — the "log out
/// everywhere" action. Scoped strictly to the caller's own user id: another user's sessions
/// are never touched, regardless of how this endpoint is invoked.
/// </summary>
public static class LogoutAllHandler
{
    public static async Task<int> ExecuteAsync(
        ClaimsPrincipal caller,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userIdClaim = caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            // No recognizable user id — nothing to revoke.
            return 0;
        }

        var activeSessions = await dbContext.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ToListAsync(cancellationToken);

        if (activeSessions.Count == 0)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        foreach (var session in activeSessions)
        {
            session.RevokedAt = now;
            session.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return activeSessions.Count;
    }
}
