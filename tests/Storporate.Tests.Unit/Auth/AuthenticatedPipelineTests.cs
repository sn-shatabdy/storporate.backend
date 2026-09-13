using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Auth;

/// <summary>
/// Regression coverage for the STOR-61 Phase 3 bug where a real, validly-issued access token
/// caused every [Authorize] endpoint to fail to find its own claims. Root cause: ASP.NET Core's
/// <c>JwtSecurityTokenHandler</c> remaps standard claim types (<c>sub</c> -&gt;
/// <c>ClaimTypes.NameIdentifier</c>, <c>email</c> -&gt; <c>ClaimTypes.Email</c>, ...) on the
/// inbound <see cref="System.Security.Claims.ClaimsPrincipal"/> unless
/// <c>JwtBearerOptions.MapInboundClaims</c> is explicitly set to <c>false</c> — which
/// <c>Program.cs</c>'s <c>AddJwtBearer</c> configuration did not do.
///
/// This class is the important addition: unlike <c>GetCurrentUserHandlerTests</c>,
/// <c>LogoutHandlerTests</c>, <c>LogoutAllHandlerTests</c>, and <c>ListSessionsHandlerTests</c>
/// (which call the handlers directly with a hand-built <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// and therefore never exercise ASP.NET Core's real inbound-claim-mapping behavior), every test
/// here mints its access token via the real <see cref="IJwtTokenService"/> and sends it through
/// a real HTTP request against a real <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// (<see cref="AuthEndpointsFactory"/>), so the real <c>AddAuthentication().AddJwtBearer(...)</c>
/// pipeline configured in <c>Program.cs</c> is what validates the token and builds the
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> the handler sees. Reverting the
/// <c>MapInboundClaims = false</c> fix in <c>Program.cs</c> makes every test in this class fail.
/// </summary>
public class AuthenticatedPipelineTests : IClassFixture<AuthEndpointsFactory>
{
    // ASP.NET Core's default minimal-API JSON options serialize response bodies with a
    // camelCase naming policy (e.g. "userId", "revokedSessions"); System.Net.Http.Json's
    // ReadFromJsonAsync<T> defaults to case-sensitive matching against the C# (PascalCase)
    // property names, so deserialization must opt into JsonSerializerDefaults.Web explicitly
    // or every field silently binds to its default value instead of throwing.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AuthEndpointsFactory _factory;

    public AuthenticatedPipelineTests(AuthEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetMe_WithRealAccessToken_Returns200WithClaimsDerivedFields()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();

        var user = await SeedUserAsync(dbContext, "me-pipeline@example.com", ActorTypes.Organization, VerificationStatuses.Unverified);
        var tokens = await tokenService.IssueTokensAsync(user, "integration-test-agent", CancellationToken.None);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/auth/me");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /api/auth/me with a real, freshly-issued access token, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");

        var payload = await response.Content.ReadFromJsonAsync<GetCurrentUserResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(user.Id, payload!.UserId);
        Assert.Equal("me-pipeline@example.com", payload.Email);
        Assert.Equal(ActorTypes.Organization, payload.ActorType);
        Assert.Equal(VerificationStatuses.Unverified, payload.VerificationStatus);
    }

    [Fact]
    public async Task LogoutAll_WithRealAccessToken_RevokesAllSessions_AndReportsNonZeroCount()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();

        var user = await SeedUserAsync(dbContext, "logout-all-pipeline@example.com", ActorTypes.Student, VerificationStatuses.Verified);

        // Two independent sessions for the same user, e.g. two separate device logins.
        var sessionOneTokens = await tokenService.IssueTokensAsync(user, "device-one", CancellationToken.None);
        var sessionTwoTokens = await tokenService.IssueTokensAsync(user, "device-two", CancellationToken.None);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionOneTokens.AccessToken);

        var response = await client.PostAsJsonAsync("/api/auth/logout-all", new { });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /api/auth/logout-all with a real access token, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");

        var payload = await response.Content.ReadFromJsonAsync<LogoutAllResponseBody>(JsonOptions);
        Assert.NotNull(payload);

        // This is the "worse case" from the bug report: before the MapInboundClaims fix, the
        // handler silently fell into its "no recognizable user id" branch and reported success
        // with revokedSessions == 0, having revoked nothing. It must actually revoke both sessions.
        Assert.Equal(2, payload!.RevokedSessions);

        // AsNoTracking: `dbContext` already has these Session entities tracked from the
        // SeedUserAsync/IssueTokensAsync calls above (the HTTP request revoked them via a
        // *different*, request-scoped WriteDbContext instance that wrote straight to the
        // in-memory store). Without AsNoTracking, EF Core's identity resolution would hand back
        // the stale, already-tracked instances instead of re-reading the updated values.
        var reloadedSessions = await dbContext.Sessions
            .AsNoTracking()
            .Where(s => s.UserId == user.Id)
            .ToListAsync();
        Assert.Equal(2, reloadedSessions.Count);
        Assert.All(reloadedSessions, s => Assert.NotNull(s.RevokedAt));
    }

    [Fact]
    public async Task Logout_WithRealAccessToken_RevokesOnlyTheCallingSession()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();

        var user = await SeedUserAsync(dbContext, "logout-pipeline@example.com", ActorTypes.Student, VerificationStatuses.Verified);

        var sessionOneTokens = await tokenService.IssueTokensAsync(user, "device-one", CancellationToken.None);
        var sessionTwoTokens = await tokenService.IssueTokensAsync(user, "device-two", CancellationToken.None);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionOneTokens.AccessToken);

        var response = await client.PostAsJsonAsync("/api/auth/logout", new { });

        Assert.True(
            response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 204 from /api/auth/logout with a real access token, got {(int)response.StatusCode} {response.StatusCode}.");

        var reloadedSessions = await dbContext.Sessions
            .AsNoTracking()
            .Where(s => s.UserId == user.Id)
            .ToListAsync();

        // Exactly the session identified by sessionOneTokens' sid claim was revoked; the other
        // device's session is untouched. Before the fix, the handler's sid lookup silently
        // failed and this endpoint was a no-op that still returned 204 without revoking anything.
        Assert.Single(reloadedSessions, s => s.RevokedAt is not null);
        Assert.Single(reloadedSessions, s => s.RevokedAt is null);
    }

    [Fact]
    public async Task ListSessions_WithRealAccessToken_ReturnsCallersSessions_NotEmpty()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();

        var user = await SeedUserAsync(dbContext, "sessions-pipeline@example.com", ActorTypes.Student, VerificationStatuses.Verified);

        var sessionOneTokens = await tokenService.IssueTokensAsync(user, "device-one", CancellationToken.None);
        await tokenService.IssueTokensAsync(user, "device-two", CancellationToken.None);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionOneTokens.AccessToken);

        var response = await client.GetAsync("/api/auth/sessions");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /api/auth/sessions with a real access token, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");

        var payload = await response.Content.ReadFromJsonAsync<ListSessionsResponse>(JsonOptions);
        Assert.NotNull(payload);

        // Before the fix, the "sub" claim came back remapped to ClaimTypes.NameIdentifier, so
        // the handler's `caller.FindFirst(JwtRegisteredClaimNames.Sub)` lookup found nothing and
        // it silently returned an empty list for a caller who actually has two active sessions.
        Assert.Equal(2, payload!.Sessions.Count);
        Assert.Single(payload.Sessions, s => s.IsCurrent);
    }

    private static async Task<User> SeedUserAsync(
        WriteDbContext dbContext,
        string email,
        string actorType,
        string verificationStatus)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            ActorType = actorType,
            VerificationStatus = verificationStatus,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private sealed record LogoutAllResponseBody(int RevokedSessions);
}
