using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Storporate.Tests.Unit.Auth;

/// <summary>
/// Integration-level coverage for the [Authorize] attribute on /me, /sessions, /logout, and
/// /logout-all: every endpoint must reject a request with no/invalid bearer token with 401.
/// Drives the real ASP.NET Core JwtBearer middleware (with the production JwtOptions bound)
/// using an in-memory WriteDbContext, no live Postgres required.
/// </summary>
public class AuthorizeEndpointTests : IClassFixture<AuthEndpointsFactory>
{
    private readonly AuthEndpointsFactory _factory;

    public AuthorizeEndpointTests(AuthEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetMe_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMe_WithMalformedBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-jwt");

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSessions_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/auth/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSessions_WithMalformedBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-jwt");

        var response = await client.GetAsync("/api/auth/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostLogout_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/logout", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostLogoutAll_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/logout-all", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OtpRequest_WithoutBearerToken_Returns200_NotSubjectToAuthorize()
    {
        // Sanity check: the [Authorize]-less endpoints stay accessible without a token.
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/otp/request", new { email = "anyone@example.com" });

        // 200 (request accepted, regardless of whether the email has an account — see
        // RequestOtpHandler's no-account-enumeration doc comment). May vary if the OTP handler
        // can't reach Resend, but in Development the code path returns 200 either way.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.TooManyRequests,
            $"Expected 200 (success) or 429 (rate-limited), got {response.StatusCode}.");
    }
}
