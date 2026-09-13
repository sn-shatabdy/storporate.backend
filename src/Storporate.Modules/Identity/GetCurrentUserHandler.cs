using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Storporate.Modules.Identity;

/// <summary>
/// Returns the caller's own account identity (email, actor type, verification status) sourced
/// directly from the <c>sub</c>/<c>email</c>/<c>actor_type</c>/<c>verification_status</c>
/// claims on the bearer access token. No database lookup — the JWT was issued under those
/// claims minutes ago and is the authoritative snapshot for the current request.
/// </summary>
public static class GetCurrentUserHandler
{
    public static GetCurrentUserResponse Execute(ClaimsPrincipal caller)
    {
        var userId = caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? throw new UnauthorizedAccessException("Token is missing the 'sub' claim.");
        var email = caller.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? throw new UnauthorizedAccessException("Token is missing the 'email' claim.");
        var actorType = caller.FindFirst("actor_type")?.Value
            ?? throw new UnauthorizedAccessException("Token is missing the 'actor_type' claim.");
        var verificationStatus = caller.FindFirst("verification_status")?.Value
            ?? throw new UnauthorizedAccessException("Token is missing the 'verification_status' claim.");

        if (!Guid.TryParse(userId, out var userIdGuid))
        {
            throw new UnauthorizedAccessException("Token 'sub' claim is not a valid user id.");
        }

        return new GetCurrentUserResponse(userIdGuid, email, actorType, verificationStatus);
    }
}
