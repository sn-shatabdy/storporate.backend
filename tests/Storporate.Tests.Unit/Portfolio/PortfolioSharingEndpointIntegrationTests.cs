using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// STOR-44 Phase 1: end-to-end integration coverage for
/// <c>PUT /api/portfolio/items/{id:guid}/sharing</c>. Goes through the real
/// ASP.NET Core pipeline with a real-issued access token, exercising the
/// validator, the handler, the audit writer (as <see cref="FakeAuditLogWriter"/>),
/// the talent-index refresh enqueue path, and the global query filter's
/// cross-account 404 contract.
/// </summary>
/// <remarks>
/// Pinned behaviors:
/// <list type="bullet">
///   <item>PUT as owning Student with shareOriginal=true → 200, body carries
///   <c>shareOriginalWithEmployers: true</c>, exactly one refresh job,
///   exactly one audit row carrying only the item id + new flag value.</item>
///   <item>PUT same value again → 200, body unchanged, NO additional
///   refresh job (the no-op short-circuit).</item>
///   <item>PUT as another Student → 404 (cross-account id never matches the
///   global filter; same posture as the delete endpoint).</item>
///   <item>PUT as an Organization → 403 (the role does not hold
///   <c>portfolio:update</c>).</item>
///   <item>PUT with no bearer token → 401.</item>
///   <item>PUT with empty body <c>{}</c> → 400 <c>share_original_required</c>
///   (a missing value is rejected rather than defaulted).</item>
///   <item>GET <c>/api/portfolio/items</c> returns
///   <c>shareOriginalWithEmployers: false</c> for a freshly created item.</item>
/// </list>
/// </remarks>
public class PortfolioSharingEndpointIntegrationTests : IClassFixture<PortfolioSharingEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PortfolioSharingEndpointsFactory _factory;

    public PortfolioSharingEndpointIntegrationTests(PortfolioSharingEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PutSharing_OwningStudent_FlipsToTrue_EnqueuesOneRefreshJobAndWritesAuditRow()
    {
        var user = await SeedStudentUserAsync();
        SeedSearchableProfile(user.Id, isSearchable: true);
        var itemId = await SeedLinkItemAsync(user.Id);
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync(
            $"/api/portfolio/items/{itemId}/sharing",
            new { shareOriginal = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<PortfolioItemResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.True(body!.ShareOriginalWithEmployers);

        // Exactly one refresh job was enqueued (the EnqueueIfSearchableAsync
        // gate passes because we pre-seeded IsSearchable=true). The job
        // table is the same one DeletePortfolioItemHandler writes to, so
        // a regression that double-enqueues would show up here too.
        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var refreshJobs = await db.Jobs.AsNoTracking()
            .Where(j => j.Type == TalentIndexJobTypes.RefreshEntry && j.AccountId == user.Id)
            .ToListAsync();
        var job = Assert.Single(refreshJobs);
        Assert.Equal(JobStatus.Pending, job.Status);
        var payload = JsonSerializer.Deserialize<RefreshTalentIndexPayload>(job.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(user.Id, payload!.StudentAccountId);

        // Exactly one audit row for the sharing change, with only the
        // item id and the new flag in the metadata (no file name, no URL).
        var recorded = _factory.AuditLogWriter.Recorded
            .Where(r => r.Action == "portfolio_sharing_changed")
            .ToList();
        var entry = Assert.Single(recorded);
        Assert.Equal("PortfolioItem", entry.ResourceType);
        Assert.Equal(itemId.ToString(), entry.ResourceId);
        Assert.NotNull(entry.MetadataJson);
        Assert.Contains($"\"portfolioItemId\":\"{itemId}\"", entry.MetadataJson);
        Assert.Contains("\"shareOriginal\":true", entry.MetadataJson);
        Assert.DoesNotContain("fileName", entry.MetadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", entry.MetadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", entry.MetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutSharing_SameValue_IsNoOpAndDoesNotEnqueueSecondJob()
    {
        var user = await SeedStudentUserAsync();
        SeedSearchableProfile(user.Id, isSearchable: true);
        var itemId = await SeedLinkItemAsync(user.Id, shareOriginal: true);
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync(
            $"/api/portfolio/items/{itemId}/sharing",
            new { shareOriginal = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Same value: no refresh job. The Job count must remain at zero.
        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var refreshJobs = await db.Jobs.AsNoTracking()
            .Where(j => j.Type == TalentIndexJobTypes.RefreshEntry && j.AccountId == user.Id)
            .ToListAsync();
        Assert.Empty(refreshJobs);
    }

    [Fact]
    public async Task PutSharing_OtherStudent_Returns404()
    {
        var owner = await SeedStudentUserAsync();
        SeedSearchableProfile(owner.Id, isSearchable: true);
        var itemId = await SeedLinkItemAsync(owner.Id);

        var otherStudent = await SeedStudentUserAsync($"other-{Guid.NewGuid():N}@example.com");
        var tokens = await IssueTokensAsync(otherStudent);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync(
            $"/api/portfolio/items/{itemId}/sharing",
            new { shareOriginal = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PutSharing_Organization_Returns403()
    {
        var owner = await SeedStudentUserAsync();
        SeedSearchableProfile(owner.Id, isSearchable: true);
        var itemId = await SeedLinkItemAsync(owner.Id);

        var org = await SeedOrganizationUserAsync($"org-{Guid.NewGuid():N}@example.com");
        var tokens = await IssueTokensAsync(org);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync(
            $"/api/portfolio/items/{itemId}/sharing",
            new { shareOriginal = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutSharing_NoBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/api/portfolio/items/{Guid.NewGuid()}/sharing",
            new { shareOriginal = true });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutSharing_MissingField_Returns400ShareOriginalRequired()
    {
        var user = await SeedStudentUserAsync();
        SeedSearchableProfile(user.Id, isSearchable: true);
        var itemId = await SeedLinkItemAsync(user.Id);
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        // Empty body — the validator rejects a missing shareOriginal with
        // the stable share_original_required error code.
        var response = await client.PutAsJsonAsync(
            $"/api/portfolio/items/{itemId}/sharing",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errorCode = await ReadErrorCodeAsync(response);
        Assert.Equal("share_original_required", errorCode);
    }

    [Fact]
    public async Task GetPortfolioItems_NewItem_ShareOriginalWithEmployersIsFalse()
    {
        // Wire-shape contract for the list endpoint: a freshly-created item
        // has shareOriginalWithEmployers=false on the wire (matching the
        // SQL DEFAULT and the documented "off by default" semantics).
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        using (var seedScope = _factory.Services.CreateScope())
        {
            var accountContext = seedScope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
            accountContext.SetUserId(user.Id);
            accountContext.SetAccountId(user.Id);
            accountContext.SetIsAdministrator(false);
            var db = seedScope.ServiceProvider.GetRequiredService<WriteDbContext>();
            db.PortfolioItems.Add(new PortfolioItem
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                Label = "Freshly created item",
                Category = PortfolioCategories.Document,
                SubmissionType = PortfolioSubmissionTypes.Link,
                ExternalUrl = "https://example.com/fresh",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/portfolio/items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        var firstRow = json.RootElement.GetProperty("items")[0];

        // Wire casing: camelCase, matching the rest of the API.
        Assert.True(firstRow.TryGetProperty("shareOriginalWithEmployers", out var flag));
        Assert.Equal(JsonValueKind.False, flag.ValueKind);
    }

    // ----- infrastructure -----

    private async Task<User> SeedStudentUserAsync(string? emailSuffix = null) =>
        await SeedUserAsync(ActorTypes.Student, emailSuffix ?? $"sharing-test-{Guid.NewGuid():N}@example.com");

    private async Task<User> SeedOrganizationUserAsync(string email) =>
        await SeedUserAsync(ActorTypes.Organization, email);

    private async Task<User> SeedUserAsync(string actorType, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();

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
        return user;
    }

    private void SeedSearchableProfile(Guid accountId, bool isSearchable)
    {
        using var scope = _factory.Services.CreateScope();
        // StudentSearchProfile is IAccountScoped — must establish the
        // ambient account context before saving so RowLevelSecurityInterceptor
        // sees the right principal.
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);
        var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        db.StudentSearchProfiles.Add(new StudentSearchProfile
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            IsSearchable = isSearchable,
            DisplayName = isSearchable ? "Seed Student" : string.Empty,
            OptedInAt = isSearchable ? DateTimeOffset.UtcNow : null,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private async Task<Guid> SeedLinkItemAsync(Guid accountId, bool shareOriginal = false)
    {
        using var scope = _factory.Services.CreateScope();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);
        var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var item = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Label = "Item",
            Category = PortfolioCategories.Document,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = "https://example.com/x",
            CreatedAt = DateTimeOffset.UtcNow,
            ShareOriginalWithEmployers = shareOriginal,
        };
        db.PortfolioItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private async Task<AuthTokenResult> IssueTokensAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        return await tokenService.IssueTokensAsync(user, "sharing-test", CancellationToken.None);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("errorCode", out var errorCode))
        {
            return errorCode.GetString();
        }
        return null;
    }
}
