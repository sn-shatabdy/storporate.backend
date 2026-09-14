using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;

namespace Storporate.Modules.Identity;

/// <summary>
/// Lists the caller's non-revoked, non-expired sessions, marking the one identified by the
/// caller's <c>sid</c> claim as <c>IsCurrent = true</c> so the account page can render the
/// "this device" badge. Hashed refresh tokens are never returned; the session id alone is
/// sufficient to drive per-session logout.
/// </summary>
public static class ListSessionsHandler
{
    public static async Task<ListSessionsResponse> ExecuteAsync(
        ClaimsPrincipal caller,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userIdClaim = caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return new ListSessionsResponse([]);
        }

        var currentSessionIdClaim = caller.FindFirst(JwtRegisteredClaimNames.Sid)?.Value;
        var currentSessionId = currentSessionIdClaim is null
            ? (Guid?)null
            : (Guid.TryParse(currentSessionIdClaim, out var parsed) ? parsed : null);

        var now = DateTime.UtcNow;
        var activeSessions = await dbContext.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ListSessionsSessionItem(
                s.Id,
                s.CreatedAt,
                s.UserAgent,
                currentSessionId.HasValue && s.Id == currentSessionId.Value))
            .ToListAsync(cancellationToken);

        return new ListSessionsResponse(activeSessions);
    }
}
