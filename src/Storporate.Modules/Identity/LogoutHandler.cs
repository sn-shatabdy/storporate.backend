using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Identity;

/// <summary>
/// Revokes the single <see cref="Session"/> identified by the <c>sid</c> claim of the caller's
/// access token. Idempotent: revoking a session that is already revoked (or has been rotated
/// out via logout-all) is a no-op, not an error — the caller's intent ("this device should not
/// be logged in anymore") is already satisfied.
/// </summary>
public static class LogoutHandler
{
    public static async Task ExecuteAsync(
        ClaimsPrincipal caller,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var sessionIdClaim = caller.FindFirst(JwtRegisteredClaimNames.Sid)?.Value;
        if (sessionIdClaim is null || !Guid.TryParse(sessionIdClaim, out var sessionId))
        {
            // The token has no sid claim — either it was issued before Phase 3 or it was forged.
            // Treat as no-op (no session to revoke); idempotent logout.
            return;
        }

        var session = await dbContext.Sessions.FirstOrDefaultAsync(
            s => s.Id == sessionId,
            cancellationToken);
        if (session is null)
        {
            return;
        }

        if (session.RevokedAt is null)
        {
            var now = DateTime.UtcNow;
            session.RevokedAt = now;
            session.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
