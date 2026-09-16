using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Identity;

/// <summary>
/// Revokes the single <see cref="Session"/> identified by the <c>sid</c> claim of the caller's
/// access token. Idempotent: revoking a session that is already revoked (or has been rotated
/// out via logout-all) is a no-op, not an error — the caller's intent ("this device should not
/// be logged in anymore") is already satisfied.
/// </summary>
/// <remarks>
/// Writes one <c>"session_revoked"</c> audit row only when the handler
/// actually flipped <c>RevokedAt</c> from null — the existing idempotency check
/// (<c>if (session.RevokedAt is null)</c>) is the audit gate too, so a re-logout of an
/// already-revoked session records nothing (it didn't happen, semantically). This matches the
/// "real event happened" invariant the rest of the audit instrumentation pins: we record
/// what actually occurred.
/// </remarks>
public static class LogoutHandler
{
    public static async Task ExecuteAsync(
        ClaimsPrincipal caller,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        var sessionIdClaim = caller.FindFirst(JwtRegisteredClaimNames.Sid)?.Value;
        if (sessionIdClaim is null || !Guid.TryParse(sessionIdClaim, out var sessionId))
        {
            // No sid claim on the token — treat as no-op (no session to revoke);
            // idempotent logout.
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

            await auditLogWriter.WriteAsync(
                action: "session_revoked",
                resourceType: "Session",
                resourceId: session.Id.ToString(),
                cancellationToken: cancellationToken);
        }
    }
}
