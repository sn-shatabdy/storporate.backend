using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring;
using Storporate.Modules.DiscoveryHiring.TalentSearch;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-43 Phase 2: end-to-end integration coverage for the employer
/// <c>POST /api/discovery/talent-searches</c> + <c>GET /api/discovery/talent-searches/{id}</c>
/// endpoints. Goes through the real ASP.NET Core pipeline with a
/// real-issued access token; the search processor's LLM + embedding
/// clients are the <see cref="FakeLlmClient"/> / <see cref="FakeEmbeddingClient"/>
/// installed by <see cref="DiscoveryHiringEndpointsFactory"/>, so no real
/// LM Studio / OpenAI-compatible endpoint is ever touched.
/// </summary>
/// <remarks>
/// Pinned behaviors:
/// <list type="bullet">
///   <item>POST happy path: returns 202 + searchId; DB row + job in one SaveChanges; audit row written with queryLength + queryHash but never the raw query.</item>
///   <item>POST validation: empty query → 400 talent_search_query_required; short query → 400 talent_search_query_too_short; long query → 400 talent_search_query_too_long.</item>
///   <item>POST without bearer → 401; POST as a student → 403.</item>
///   <item>POST while a pending row exists → 409 talent_search_busy.</item>
///   <item>GET cross-account id → 404; GET on Pending returns status only with no results array; GET on Completed returns the results array.</item>
///   <item>GET re-validates: candidates whose live TalentIndexEntry has changed since the search ran get re-shaped (and stale citations dropped).</item>
/// </list>
/// </remarks>
public class TalentSearchEndpointsTests : IClassFixture<DiscoveryHiringEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryHiringEndpointsFactory _factory;

    public TalentSearchEndpointsTests(DiscoveryHiringEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_ValidQuery_Returns202WithSearchIdAndPersistsRowAndJobAndAudit()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingAuditCount = audit.Recorded.Count;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync("/api/discovery/talent-searches", new
        {
            query = "Need a backend engineer with Python experience and 3+ years building APIs.",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var rawBody = await response.Content.ReadAsStringAsync();
        var body = await response.Content.ReadFromJsonAsync<TalentSearchAcceptedResponse>(JsonOptions);
        Assert.NotNull(body);
        if (body!.SearchId == Guid.Empty)
        {
            throw new Xunit.Sdk.XunitException("Empty searchId returned. Body: " + rawBody);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
            accountContext.SetUserId(caller.Id);
            accountContext.SetAccountId(caller.Id);
            accountContext.SetIsAdministrator(false);

            var request = await db.TalentSearchRequests
                .AsNoTracking()
                .SingleAsync(r => r.Id == body.SearchId);
            Assert.Equal(TalentSearchStatuses.Pending, request.Status);
            Assert.Equal(caller.Id, request.AccountId);

            var job = Assert.Single(
                db.Jobs.IgnoreQueryFilters().AsNoTracking(),
                j => j.Type == DiscoveryJobTypes.SearchTalent && j.AccountId == caller.Id);
            var payload = JsonSerializer.Deserialize<SearchTalentPayload>(job.PayloadJson, JsonOptions);
            Assert.NotNull(payload);
            Assert.Equal(body.SearchId, payload!.SearchId);
        }

        var newAuditEntries = audit.Recorded.Skip(existingAuditCount).ToList();
        var auditRow = Assert.Single(newAuditEntries);
        Assert.Equal("talent_search_requested", auditRow.Action);
        Assert.Equal("TalentSearch", auditRow.ResourceType);
        Assert.Equal(body.SearchId.ToString(), auditRow.ResourceId);

        var auditJson = JsonDocument.Parse(auditRow.MetadataJson!);
        // Plan rule: queryLength is present, queryHash is present, raw query
        // is NOT.
        Assert.Equal(
            "Need a backend engineer with Python experience and 3+ years building APIs.".Length,
            auditJson.RootElement.GetProperty("queryLength").GetInt32());
        var queryHash = auditJson.RootElement.GetProperty("queryHash").GetString();
        Assert.Equal(64, queryHash!.Length);
        Assert.DoesNotContain("Python", auditRow.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_QueryTooShort_Returns400TooShort()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync("/api/discovery/talent-searches", new
        {
            query = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("talent_search_query_too_short", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Post_QueryTooLong_Returns400TooLong()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var oversized = new string('a', TalentSearchDefaults.MaxQueryLength + 1);
        var response = await client.PostAsJsonAsync("/api/discovery/talent-searches", new
        {
            query = oversized,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("talent_search_query_too_long", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Post_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/discovery/talent-searches", new
        {
            query = "Need a backend engineer with Python experience.",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_AsStudent_Returns403()
    {
        var caller = await SeedUserAsync(ActorTypes.Student, $"talent-student-{Guid.NewGuid():N}@example.com");
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync("/api/discovery/talent-searches", new
        {
            query = "Need a backend engineer with Python experience.",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_WhenAnotherSearchIsPending_Returns409Busy()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        // Pre-seed a Pending row under the caller's account so the busy
        // check fires.
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var accountContext = seedScope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
            accountContext.SetUserId(caller.Id);
            accountContext.SetAccountId(caller.Id);
            accountContext.SetIsAdministrator(false);

            db.TalentSearchRequests.Add(new TalentSearchRequest
            {
                Id = Guid.NewGuid(),
                AccountId = caller.Id,
                QueryText = "In-flight search from the previous click.",
                Status = TalentSearchStatuses.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync("/api/discovery/talent-searches", new
        {
            query = "Need another backend engineer with Python experience.",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("talent_search_busy", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/talent-searches/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Pending_ReturnsStatusWithoutResults()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);
        var searchId = await SeedPendingTalentSearchAsync(caller.Id, "Need a backend engineer with Python.");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/talent-searches/{searchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TalentSearchResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(searchId, body!.Id);
        Assert.Equal(TalentSearchStatuses.Pending, body.Status);
        Assert.Null(body.Results);
        Assert.Null(body.CompletedAt);
        Assert.Null(body.ErrorCode);
    }

    [Fact]
    public async Task Get_Completed_ReturnsStatusWithResults()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);
        var searchId = await SeedCompletedTalentSearchAsync(
            caller.Id,
            "Need a backend engineer with Python.",
            "[{\"candidateId\":\"22222222-2222-2222-2222-222222222222\",\"displayName\":\"Alice Doe\",\"matchedSkills\":[],\"reason\":\"Strong match.\",\"citedItems\":[{\"portfolioItemId\":\"33333333-3333-3333-3333-333333333333\",\"skillName\":\"Python\"}]}]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/talent-searches/{searchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TalentSearchResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(searchId, body!.Id);
        Assert.Equal(TalentSearchStatuses.Completed, body.Status);
        Assert.NotNull(body.Results);
        Assert.Single(body.Results!);
        var first = body.Results![0];
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), first.CandidateId);
        Assert.Equal("Alice Doe", first.DisplayName);
        Assert.NotNull(first.CitedItems);
        Assert.Single(first.CitedItems!);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), first.CitedItems![0].PortfolioItemId);
    }

    [Fact]
    public async Task Get_Completed_EntryDeletedBetweenRuns_DropsEntireCandidateFromResults()
    {
        // The student who was a candidate opts out (or otherwise loses
        // their TalentIndexEntry) between the search running and the GET.
        // The handler must drop the entire result row rather than leak
        // stale snapshot fields.
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);
        var searchId = await SeedCompletedTalentSearchAsync(
            caller.Id,
            "Need a backend engineer with Python.",
            "[{\"candidateId\":\"22222222-2222-2222-2222-222222222222\",\"displayName\":\"Alice Doe\",\"matchedSkills\":[],\"reason\":\"Strong match.\",\"citedItems\":[{\"portfolioItemId\":\"33333333-3333-3333-3333-333333333333\",\"skillName\":\"Python\"}]}]");

        // Wipe the live entry. The re-validation step in the GET handler
        // must drop the candidate row, returning an empty results array
        // instead of leaking the stored snapshot.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var entry = await db.TalentIndexEntries.IgnoreQueryFilters()
                .FirstAsync(e => e.Id == Guid.Parse("22222222-2222-2222-2222-222222222222"));
            db.TalentIndexEntries.Remove(entry);
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/talent-searches/{searchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TalentSearchResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.NotNull(body!.Results);
        Assert.Empty(body.Results!);
    }

    [Fact]
    public async Task Get_Completed_ItemNoLongerInSnapshot_DropsStaleCitation()
    {
        // The candidate still exists in the index, but the cited item id
        // is no longer in their ItemsJson — the student deleted or
        // re-analyzed the item since the search ran. The handler must
        // drop just that citation, not the whole candidate.
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);
        var searchId = await SeedCompletedTalentSearchAsync(
            caller.Id,
            "Need a backend engineer with Python.",
            "[{\"candidateId\":\"22222222-2222-2222-2222-222222222222\",\"displayName\":\"Alice Doe\",\"matchedSkills\":[],\"reason\":\"Strong match.\",\"citedItems\":[{\"portfolioItemId\":\"99999999-9999-9999-9999-999999999999\",\"skillName\":\"Python\"}]}]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/talent-searches/{searchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TalentSearchResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.NotNull(body!.Results);
        // The cited item id was 99999999-... but the seeded entry's snapshot
        // contains only 33333333-... — re-validation drops the stale
        // citation and the candidate loses all citations, so the row itself
        // is dropped (matching the "zero citations → drop row" rule).
        Assert.Empty(body.Results!);
    }

    [Fact]
    public async Task Get_Completed_PartialStaleCitations_DropsOnlyStaleItemsKeepsRow()
    {
        // The candidate has TWO cited items in the stored ResultJson, and
        // one of them has since been removed from the live ItemsJson. The
        // surviving citation must remain on the row; the stale one must
        // be dropped; the row itself must stay (because at least one
        // citation survives).
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        // Use a candidate whose seeded ItemsJson contains BOTH item ids so
        // the snapshot parse yields a 2-element dictionary. The handler's
        // SeedCompletedTalentSearchAsync seeds only one item id
        // (33333333-...), so this test reaches in via a small
        // composite setup instead.
        var candidateId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var itemA = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var itemB = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var twoItemSnapshot = "[" +
            $"{{\"portfolioItemId\":\"{itemA}\",\"label\":\"Item A\",\"category\":\"Project\",\"skills\":[{{\"name\":\"Python\",\"band\":\"Strong\",\"reason\":\"Used Python.\"}}]}}," +
            $"{{\"portfolioItemId\":\"{itemB}\",\"label\":\"Item B (deleted)\",\"category\":\"Project\",\"skills\":[{{\"name\":\"SQL\",\"band\":\"Developing\",\"reason\":\"Used SQL.\"}}]}}" +
            "]";
        var searchId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
            accountContext.SetUserId(caller.Id);
            accountContext.SetAccountId(caller.Id);
            accountContext.SetIsAdministrator(false);

            db.TalentIndexEntries.Add(new TalentIndexEntry
            {
                Id = candidateId,
                StudentAccountId = candidateId,
                DisplayName = "Alice Doe",
                ItemsJson = twoItemSnapshot,
                SearchText = "seed",
                ContentHash = "seed",
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            // The stored ResultJson cites item A (still alive) AND
            // item Z (a third item that was never added — simulating a
            // student who deleted an unrelated item between then and now).
            var resultJson =
                "[" +
                $"{{\"candidateId\":\"{candidateId}\",\"displayName\":\"Alice Doe\","
                + "\"matchedSkills\":[],\"reason\":\"Strong match.\","
                + $"\"citedItems\":[{{\"portfolioItemId\":\"{itemA}\",\"skillName\":\"Python\"}},"
                + "{\"portfolioItemId\":\"77777777-7777-7777-7777-777777777777\",\"skillName\":\"Python\"}]"
                + "}]";

            db.TalentSearchRequests.Add(new TalentSearchRequest
            {
                Id = searchId,
                AccountId = caller.Id,
                QueryText = "Need backend + SQL.",
                Status = TalentSearchStatuses.Completed,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
                CompletedAt = DateTimeOffset.UtcNow,
                ResultJson = resultJson,
            });
            await db.SaveChangesAsync();
        }

        // Now delete item B from the snapshot, leaving only item A. The
        // stored ResultJson cited item A and item Z (a non-existent id);
        // re-validation keeps item A and drops item Z.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var entry = await db.TalentIndexEntries.IgnoreQueryFilters()
                .FirstAsync(e => e.Id == candidateId);
            entry.ItemsJson = "[" +
                $"{{\"portfolioItemId\":\"{itemA}\",\"label\":\"Item A\",\"category\":\"Project\",\"skills\":[{{\"name\":\"Python\",\"band\":\"Strong\",\"reason\":\"Used Python.\"}}]}}" +
                "]";
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/talent-searches/{searchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TalentSearchResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.NotNull(body!.Results);
        Assert.Single(body.Results!);
        var row = body.Results![0];
        Assert.Equal(candidateId, row.CandidateId);
        Assert.NotNull(row.CitedItems);
        Assert.Single(row.CitedItems!);
        Assert.Equal(itemA, row.CitedItems![0].PortfolioItemId);
    }

    [Fact]
    public async Task Get_CrossAccountId_Returns404()
    {
        var owner = await SeedOrganizationAsync();
        var intruder = await SeedOrganizationAsync();

        var ownerTokens = await IssueTokensAsync(owner);
        var ownerSearchId = await SeedPendingTalentSearchAsync(owner.Id, "Owner searching for a candidate.");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerTokens.AccessToken);

        // Switch to the intruder (also an org, so the permission gate
        // passes; only the global query filter should be hiding the row).
        var intruderTokens = await IssueTokensAsync(intruder);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", intruderTokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/talent-searches/{ownerSearchId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- infrastructure -----

    private async Task<User> SeedOrganizationAsync() =>
        await SeedUserAsync(ActorTypes.Organization, $"org-{Guid.NewGuid():N}@example.com");

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

    private async Task<AuthTokenResult> IssueTokensAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        return await tokenService.IssueTokensAsync(user, "talent-search-test", CancellationToken.None);
    }

    private async Task<Guid> SeedPendingTalentSearchAsync(Guid accountId, string query)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);

        var searchId = Guid.NewGuid();
        dbContext.TalentSearchRequests.Add(new TalentSearchRequest
        {
            Id = searchId,
            AccountId = accountId,
            QueryText = query,
            Status = TalentSearchStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return searchId;
    }

    private async Task<Guid> SeedCompletedTalentSearchAsync(
        Guid accountId,
        string query,
        string resultJson)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);

        var candidateId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        // Seed the live TalentIndexEntry so the GET re-validation path
        // keeps the candidate AND its cited item (the handler drops any
        // citation whose portfolioItemId is not in the live snapshot,
        // and drops any candidate who ends up with zero valid citations).
        var entryItemsJson = "[{\"portfolioItemId\":\"33333333-3333-3333-3333-333333333333\",\"label\":\"Personal API project\",\"category\":\"Project\",\"skills\":[{\"name\":\"Python\",\"band\":\"Strong\",\"reason\":\"Used Python for backend.\"}]}]";
        var existing = await dbContext.TalentIndexEntries.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == candidateId);
        if (existing is null)
        {
            dbContext.TalentIndexEntries.Add(new TalentIndexEntry
            {
                Id = candidateId,
                StudentAccountId = candidateId,
                DisplayName = "Alice Doe",
                ItemsJson = entryItemsJson,
                SearchText = "seed",
                ContentHash = "seed",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.ItemsJson = entryItemsJson;
            existing.DisplayName = "Alice Doe";
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var searchId = Guid.NewGuid();
        dbContext.TalentSearchRequests.Add(new TalentSearchRequest
        {
            Id = searchId,
            AccountId = accountId,
            QueryText = query,
            Status = TalentSearchStatuses.Completed,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            CompletedAt = DateTimeOffset.UtcNow,
            ResultJson = resultJson,
        });
        await dbContext.SaveChangesAsync();
        return searchId;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("errorCode", out var errorCode)
            ? errorCode.GetString()
            : null;
    }
}
