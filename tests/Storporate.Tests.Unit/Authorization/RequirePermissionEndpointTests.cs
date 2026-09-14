using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Api.Authorization;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Authorization;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;

namespace Storporate.Tests.Unit.Authorization;

/// <summary>
/// Integration coverage for STOR-62 Phase 3's authorization plumbing. Drives the real
/// ASP.NET Core <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/> /
/// <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider"/> pipeline
/// (production-registered in <c>Program.cs</c>) against a real
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>:
/// <see cref="PermissionAuthorizationHandler"/> short-circuits to success for an
/// <see cref="ActorTypes.Administrator"/> caller, delegates to
/// <see cref="IPermissionService.HasPermissionAsync"/> for everyone else, and fails
/// closed (403) when the caller has no matching permission.
/// </summary>
/// <remarks>
/// <para>
/// The test-only minimal-API endpoint is mapped at test time via
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}.WithWebHostBuilder(System.Action{Microsoft.AspNetCore.Hosting.IWebHostBuilder})"/>
/// so it isn't part of the production API surface. The endpoint requires
/// <see cref="Permissions.Jobs.Write"/> — a permission only Organizations and Administrators
/// hold per the <see cref="SystemRoles"/> grant matrix — which gives the three test cases a
/// meaningful shape (Student has neither; Organization has it; Administrator is bypassed)
/// without needing to invent a custom permission literal.
/// </para>
/// <para>
/// Each test seeds its own <see cref="User"/> into the in-memory <see cref="WriteDbContext"/>
/// before issuing its access token so the tests are self-contained and parallel-safe (each
/// test method resolves its own scope and creates a unique email per call).
/// </para>
/// </remarks>
public class RequirePermissionEndpointTests : IClassFixture<AuthEndpointsFactory>
{
    private const string PermissionTestPath = "/api/test/require-permission/jobs-write";

    private readonly AuthEndpointsFactory _factory;

    public RequirePermissionEndpointTests(AuthEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RequirePermission_NonAdministrator_WithoutPermission_Returns403()
    {
        // Student has only Permissions.Jobs.Read per SystemRoles.Grants — and we don't grant
        // Permissions.Jobs.Write here, so the user definitely lacks the test endpoint's
        // required permission. Expected: 403 Forbidden from the authorization pipeline.
        using var client = CreateClientWithTestEndpoint();
        var (token, _) = await IssueTokenAsync(ActorTypes.Student, "no-perm@example.com");

        using var request = new HttpRequestMessage(HttpMethod.Get, PermissionTestPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RequirePermission_NonAdministrator_WithPermission_Returns200()
    {
        // Organization has both Permissions.Jobs.Read and Permissions.Jobs.Write per
        // SystemRoles.Grants — granting it the test endpoint's required permission.
        // Expected: 200 OK from the handler.
        using var client = CreateClientWithTestEndpoint();
        var (token, _) = await IssueTokenAsync(ActorTypes.Organization, "with-perm@example.com");

        using var request = new HttpRequestMessage(HttpMethod.Get, PermissionTestPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RequirePermission_Administrator_Returns200RegardlessOfPermission()
    {
        // Administrator holds Permissions.All per SystemRoles.Grants. The handler also
        // short-circuits to success when IAccountContext.IsAdministrator is true (the
        // workspace-isolation bypass path), so the test exercises both the permission-set
        // bypass and the explicit handler short-circuit — both must succeed for 200.
        using var client = CreateClientWithTestEndpoint();
        var (token, _) = await IssueTokenAsync(ActorTypes.Administrator, "admin@example.com");

        using var request = new HttpRequestMessage(HttpMethod.Get, PermissionTestPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Returns a client whose host has had a single test-only
    /// <see cref="RequirePermissionAttribute"/>-tagged minimal-API endpoint mapped onto it.
    /// Built via <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder(System.Action{Microsoft.AspNetCore.Hosting.IWebHostBuilder})"/>
    /// so the endpoint never enters the production API surface.</summary>
    private HttpClient CreateClientWithTestEndpoint()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAccountContext();
                app.UseAuthorization();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet(PermissionTestPath, () => Results.Ok(new { ok = true }))
                        .RequirePermission(Permissions.Jobs.Write);
                });
            });
        }).CreateClient();
    }

    /// <summary>Seeds a fresh <see cref="User"/> with the given actor type, issues a real
    /// access token via <see cref="IJwtTokenService.IssueTokensAsync"/>, and returns
    /// (accessToken, userId) for the caller to wire into its HTTP request.</summary>
    private async Task<(string AccessToken, Guid UserId)> IssueTokenAsync(string actorType, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            ActorType = actorType,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var tokens = await tokenService.IssueTokensAsync(user, "integration-test-agent", CancellationToken.None);
        return (tokens.AccessToken, user.Id);
    }
}
