using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Storporate.Modules.Identity;

namespace Storporate.Tests.Unit.Identity;

/// <summary>
/// TDD coverage for <see cref="GetCurrentUserHandler.Execute"/>: pulls email, actor type, and
/// verification status directly off the access-token claims (no DB lookup). The matching
/// "no/invalid bearer token → 401" rule is enforced by ASP.NET Core's [Authorize] middleware
/// in production; the handler tests cover the in-handler guard against malformed claims.
/// </summary>
public class GetCurrentUserHandlerTests
{
    [Fact]
    public void Execute_ReturnsClaimsAsResponse()
    {
        var userId = Guid.NewGuid();
        var caller = BuildPrincipal(userId, "alice@example.com", "Organization", "Unverified");

        var response = GetCurrentUserHandler.Execute(caller);

        Assert.Equal(userId, response.UserId);
        Assert.Equal("alice@example.com", response.Email);
        Assert.Equal("Organization", response.ActorType);
        Assert.Equal("Unverified", response.VerificationStatus);
    }

    [Fact]
    public void Execute_MissingSubClaim_Throws()
    {
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Email, "no-sub@example.com")],
            authenticationType: "test"));

        Assert.Throws<UnauthorizedAccessException>(() => GetCurrentUserHandler.Execute(caller));
    }

    [Fact]
    public void Execute_MissingEmailClaim_Throws()
    {
        var userId = Guid.NewGuid();
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())],
            authenticationType: "test"));

        Assert.Throws<UnauthorizedAccessException>(() => GetCurrentUserHandler.Execute(caller));
    }

    [Fact]
    public void Execute_NonGuidSubClaim_Throws()
    {
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "not-a-guid"),
                new Claim(JwtRegisteredClaimNames.Email, "bad-sub@example.com"),
            ],
            authenticationType: "test"));

        Assert.Throws<UnauthorizedAccessException>(() => GetCurrentUserHandler.Execute(caller));
    }

    private static ClaimsPrincipal BuildPrincipal(
        Guid userId,
        string email,
        string actorType,
        string verificationStatus) =>
        new(new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim("actor_type", actorType),
                new Claim("verification_status", verificationStatus),
            ],
            authenticationType: "test"));
}
