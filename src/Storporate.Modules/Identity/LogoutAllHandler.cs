using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;

namespace Storporate.Modules.Identity;

/// <summary>
/// Revokes every non-revoked <see cref="Storporate.SharedKernel.Entities.Session"/> belonging
/// to the caller (identified by the <c>sub</c> claim of their access token) — the "log out
/// everywhere" action. Scoped strictly to the caller's own user id: another user's sessions
/// are never touched, regardless of how this endpoint is invoked.
/// </summary>
/// <remarks>
/// Writes one <c>"all_sessions_revoked"</c> row after
/// <c>SaveChangesAsync</c>, with <c>MetadataJson</c> capturing the revoked-session count so a
/// future audit-log query can distinguish "this account really did have N active sessions that
/// got killed" from "the handler ran but found nothing to revoke" (the latter returns early
/// at the <c>activeSessions.Count == 0</c> check and records nothing — same idempotent-audit
/// invariant as <see cref="LogoutHandler"/>).
/// </remarks>
public static class LogoutAllHandler
{
    public static async Task<int> ExecuteAsync(
        ClaimsPrincipal caller,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        var userIdClaim = caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
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

        await auditLogWriter.WriteAsync(
            action: "all_sessions_revoked",
            resourceType: "Session",
            resourceId: null,
            metadataJson: $$"""{"revokedCount":{{activeSessions.Count}}}""",
            cancellationToken: cancellationToken);

        return activeSessions.Count;
    }
}
