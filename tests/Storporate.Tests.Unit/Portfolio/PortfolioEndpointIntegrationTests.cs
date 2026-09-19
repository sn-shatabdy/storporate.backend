using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Storage;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// End-to-end integration coverage for <c>/api/portfolio/items</c>. Goes
/// through the real ASP.NET Core pipeline (a <see cref="WebApplicationFactory{TEntryPoint}"/>
/// stand-in) with a real-issued access token, so the validation rules the plan's
/// acceptance criteria call out (both file and URL present, neither present,
/// oversized file, Category="Other" without custom text) are exercised against the
/// actual <see cref="CreatePortfolioItemValidator"/> instance the host builds, not a
/// hand-rolled duplicate.
/// </summary>
public class PortfolioEndpointIntegrationTests : IClassFixture<AuthEndpointsFactory>
{
    private readonly AuthEndpointsFactory _factory;

    public PortfolioEndpointIntegrationTests(AuthEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostPortfolioItem_BothFileAndUrl_Returns400AndWritesNothingToStorage()
    {
        var user = await SeedStudentUserAsync();
        var artifactStore = _factory.Services.GetRequiredService<IArtifactStore>() as FakeArtifactStore
            ?? throw new InvalidOperationException("Test factory did not swap IArtifactStore for the in-memory fake; the test harness is misconfigured.");

        var tokens = await IssueTokensAsync(user);

        var storedKeysBefore = artifactStore.StoredKeys.Count;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 }), "file", "doc.pdf");
        form.Add(new StringContent("Document"), "category");
        form.Add(new StringContent("Final report"), "label");
        form.Add(new StringContent("https://example.com/portfolio"), "externalUrl");

        var response = await client.PostAsync("/api/portfolio/items", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errorCode = await ReadErrorCodeAsync(response);
        Assert.Equal("submission_source_conflict", errorCode);

        // Critical: no PutAsync fired — the oversized / conflict / etc. cases
        // must short-circuit BEFORE storage write.
        Assert.Equal(storedKeysBefore, artifactStore.StoredKeys.Count);
    }

    [Fact]
    public async Task PostPortfolioItem_NeitherFileNorUrl_Returns400AndWritesNothing()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Document"), "category");
        form.Add(new StringContent("Final report"), "label");

        var response = await client.PostAsync("/api/portfolio/items", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errorCode = await ReadErrorCodeAsync(response);
        Assert.Equal("submission_source_conflict", errorCode);
    }

    [Fact]
    public async Task PostPortfolioItem_OversizedFile_Returns400AndWritesNothing()
    {
        var user = await SeedStudentUserAsync();
        var artifactStore = _factory.Services.GetRequiredService<IArtifactStore>() as FakeArtifactStore
            ?? throw new InvalidOperationException("Test factory did not swap IArtifactStore for the in-memory fake; the test harness is misconfigured.");
        var storedKeysBefore = artifactStore.StoredKeys.Count;

        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        using var form = new MultipartFormDataContent();
        // 100 MB + 1 byte, just past the validator's threshold. Multipart
        // streaming is OK because the validator checks IFormFile.Length
        // (already known from the multipart section header) before the
        // handler ever streams the bytes.
        var oversizedPayload = new byte[CreatePortfolioItemValidator.MaxFileSizeBytes + 1];
        form.Add(new ByteArrayContent(oversizedPayload), "file", "huge.pdf");
        form.Add(new StringContent("Document"), "category");
        form.Add(new StringContent("Huge file"), "label");

        var response = await client.PostAsync("/api/portfolio/items", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errorCode = await ReadErrorCodeAsync(response);
        Assert.Equal("file_too_large", errorCode);
        Assert.Equal(storedKeysBefore, artifactStore.StoredKeys.Count);
    }

    [Fact]
    public async Task PostPortfolioItem_OtherCategoryWithoutCustomText_Returns400()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Other"), "category");
        form.Add(new StringContent("Some odd submission"), "label");
        form.Add(new StringContent("https://example.com/something"), "externalUrl");
        // (no customCategoryText form field)

        var response = await client.PostAsync("/api/portfolio/items", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errorCode = await ReadErrorCodeAsync(response);
        Assert.Equal("custom_category_text_required", errorCode);
    }

    [Fact]
    public async Task PostPortfolioItem_LinkSubmission_Returns201AndStoresOnlyUrl()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("PortfolioLink"), "category");
        form.Add(new StringContent("My portfolio"), "label");
        form.Add(new StringContent("https://example.com/portfolio"), "externalUrl");

        var response = await client.PostAsync("/api/portfolio/items", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // AccountContextMiddleware writes the ambient AccountId/UserId via AsyncLocal.
        // TestServer terminates the request ExecutionContext when the response is sent,
        // so by the time the test scope's DbContext resolves here the AsyncLocal is empty.
        // Re-establish the ambient context for this scope so the IAccountScoped global
        // query filter doesn't filter out the row the request just inserted.
        using var scope = _factory.Services.CreateScope();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(user.Id);
        accountContext.SetAccountId(user.Id);
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var stored = await dbContext.PortfolioItems
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(user.Id, stored.AccountId);
        Assert.Equal(PortfolioSubmissionTypes.Link, stored.SubmissionType);
        Assert.Equal("https://example.com/portfolio", stored.ExternalUrl);
        Assert.Null(stored.StorageKey);
    }

    [Fact]
    public async Task GetPortfolioItems_CarriesAnalysisStatusAndLastAnalyzedAt_OnEveryRow()
    {
        // STOR-38 Phase 3 addendum: the list endpoint surfaces the per-item
        // analysis state on every row so the Phase 5 portfolio page can render
        // its status badge without an extra API call per row. This test
        // verifies both that the fields round-trip from the entity to the JSON
        // body and that the wire-casing is camelCase (`analysisStatus` /
        // `lastAnalyzedAt`) — the same convention the rest of the API uses
        // (the GlobalExceptionHandler's `errorCode`/`message` is the precedent).
        var user = await SeedStudentUserAsync();
        var analyzedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var tokens = await IssueTokensAsync(user);

        // Seed an Analyzed item directly via the test factory's DbContext so we
        // can assert a non-default state on the wire.
        using (var seedScope = _factory.Services.CreateScope())
        {
            var accountContext = seedScope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
            accountContext.SetUserId(user.Id);
            accountContext.SetAccountId(user.Id);
            accountContext.SetIsAdministrator(false);
            var seedContext = seedScope.ServiceProvider.GetRequiredService<WriteDbContext>();
            seedContext.PortfolioItems.Add(new PortfolioItem
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                Label = "Analyzed doc",
                Category = PortfolioCategories.Document,
                SubmissionType = PortfolioSubmissionTypes.Link,
                ExternalUrl = "https://example.com/x",
                CreatedAt = DateTimeOffset.UtcNow,
                AnalysisStatus = PortfolioAnalysisStatuses.Analyzed,
                LastAnalyzedAt = analyzedAt,
            });
            await seedContext.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/portfolio/items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        var firstRow = json.RootElement.GetProperty("items")[0];

        // Wire casing: camelCase, matching the rest of the API.
        Assert.Equal(PortfolioAnalysisStatuses.Analyzed, firstRow.GetProperty("analysisStatus").GetString());
        Assert.Equal(analyzedAt.ToString("o"), firstRow.GetProperty("lastAnalyzedAt").GetDateTimeOffset().ToString("o"));
    }

    [Fact]
    public async Task GetPortfolioItems_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/portfolio/items");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetPortfolioItems_CarriesSkillsArrayPerItem_OnTheWire()
    {
        // STOR-39 Phase 1: the timeline view needs the per-item AI skill findings inline
        // (skill name + confidence-band color) so it can render one chip per finding
        // without an extra /analysis request per row. This end-to-end test seeds a mix
        // — one analyzed item with two findings, one not-analyzed item with none —
        // and asserts the wire response carries a `skills` array per item (populated
        // for the analyzed item, empty-but-not-null for the not-analyzed item). It
        // also captures the full JSON body to stdout so the wire shape is visible in
        // the test log for the orchestrator's report.
        var user = await SeedStudentUserAsync();
        var analyzedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var tokens = await IssueTokensAsync(user);

        Guid analyzedItemId;
        Guid pendingItemId;
        using (var seedScope = _factory.Services.CreateScope())
        {
            var accountContext = seedScope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
            accountContext.SetUserId(user.Id);
            accountContext.SetAccountId(user.Id);
            accountContext.SetIsAdministrator(false);
            var seedContext = seedScope.ServiceProvider.GetRequiredService<WriteDbContext>();

            var analyzedItem = new PortfolioItem
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                Label = "Analyzed doc",
                Category = PortfolioCategories.Document,
                SubmissionType = PortfolioSubmissionTypes.Link,
                ExternalUrl = "https://example.com/x",
                CreatedAt = DateTimeOffset.UtcNow,
                AnalysisStatus = PortfolioAnalysisStatuses.Analyzed,
                LastAnalyzedAt = analyzedAt,
            };
            analyzedItemId = analyzedItem.Id;
            seedContext.PortfolioItems.Add(analyzedItem);

            seedContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                PortfolioItemId = analyzedItem.Id,
                SkillName = "React",
                ConfidenceBand = ConfidenceBands.Strong,
                Explanation = "Builds a component-based UI in the description.",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            });
            seedContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                PortfolioItemId = analyzedItem.Id,
                SkillName = "Public Speaking",
                ConfidenceBand = ConfidenceBands.Developing,
                Explanation = "Mentions a class presentation but no transcript.",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });

            var pendingItem = new PortfolioItem
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                Label = "Pending doc",
                Category = PortfolioCategories.Document,
                SubmissionType = PortfolioSubmissionTypes.Link,
                ExternalUrl = "https://example.com/y",
                CreatedAt = DateTimeOffset.UtcNow,
                AnalysisStatus = PortfolioAnalysisStatuses.NotAnalyzed,
                LastAnalyzedAt = null,
            };
            pendingItemId = pendingItem.Id;
            seedContext.PortfolioItems.Add(pendingItem);

            await seedContext.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/portfolio/items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // Print the full body to the test log so the orchestrator can paste the wire
        // shape into its report — `Console.WriteLine` lands in xunit's standard output
        // capture, which the dotnet test logger emits at normal verbosity.
        Console.WriteLine($"GET /api/portfolio/items response body (STOR-39 wire shape): {body}");

        var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        var analyzedRow = Assert.Single(items.EnumerateArray(), row => row.GetProperty("id").GetGuid() == analyzedItemId);
        var pendingRow = Assert.Single(items.EnumerateArray(), row => row.GetProperty("id").GetGuid() == pendingItemId);

        var analyzedSkills = analyzedRow.GetProperty("skills");
        Assert.Equal(2, analyzedSkills.GetArrayLength());
        Assert.Contains(analyzedSkills.EnumerateArray(), s =>
            s.GetProperty("skillName").GetString() == "React"
            && s.GetProperty("confidenceBand").GetString() == ConfidenceBands.Strong);
        Assert.Contains(analyzedSkills.EnumerateArray(), s =>
            s.GetProperty("skillName").GetString() == "Public Speaking"
            && s.GetProperty("confidenceBand").GetString() == ConfidenceBands.Developing);

        // Defensive: the condensed preview has no `explanation` field on the wire —
        // the timeline must not leak the model's evidence-grounded justification.
        foreach (var skill in analyzedSkills.EnumerateArray())
        {
            Assert.False(skill.TryGetProperty("explanation", out _),
                $"Timeline skill preview should not carry an `explanation` field, but found one: {skill}");
        }

        var pendingSkills = pendingRow.GetProperty("skills");
        Assert.Equal(JsonValueKind.Array, pendingSkills.ValueKind);
        Assert.Equal(0, pendingSkills.GetArrayLength());
    }

    [Fact]
    public async Task DeletePortfolioItem_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.DeleteAsync($"/api/portfolio/items/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<User> SeedStudentUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();

        var email = $"portfolio-test-{Guid.NewGuid():N}@example.com";
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            ActorType = ActorTypes.Student,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private async Task<AuthTokenResult> IssueTokensAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        return await tokenService.IssueTokensAsync(user, "portfolio-test", CancellationToken.None);
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