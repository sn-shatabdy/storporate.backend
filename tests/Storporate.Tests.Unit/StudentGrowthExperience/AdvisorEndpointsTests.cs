using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience;
using Storporate.Modules.StudentGrowthExperience.Requests;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.StudentGrowthExperience;

/// <summary>
/// End-to-end integration coverage for the 10
/// <c>/api/growth/explorations*</c> endpoints. Goes through the real
/// ASP.NET Core pipeline with a real-issued access token; the advisor LLM
/// is the <see cref="FakeLlmClient"/> installed by
/// <see cref="AdvisorEndpointsFactory"/>.
///
/// Acceptance criteria pinned:
/// <list type="bullet">
///   <item>POST /explorations → 201, status=Working, exactly one Pending AdvisorTurn Opening job, exploration_created audit row with the right metadata shape, never with message text.</item>
///   <item>POST /messages empty body → 400 message_required; oversized body → 400 message_too_long; while Working → 409 exploration_busy.</item>
///   <item>POST /refresh on a Working exploration → 409 exploration_busy.</item>
///   <item>POST /retry on Idle → 409 exploration_not_retryable; on Failed → 202 Working with a fresh Pending job.</item>
///   <item>PUT /title without title → 400 title_required; > 200 chars → 400 title_too_long.</item>
///   <item>DELETE → 204, every dependent row gone, audit row with counts only (no message text).</item>
///   <item>POST /compare same id twice → 400 compare_needs_two; explore-no-summary → 409 exploration_has_no_summary.</item>
///   <item>Cross-account reads (GET / refresh / retry / title / delete / compare with other account's id) → 404, never 403.</item>
///   <item>21st exploration → 409 exploration_limit_reached.</item>
///   <item>Non-Student caller → 403 on a gated endpoint.</item>
/// </list>
/// </summary>
public class AdvisorEndpointsTests : IClassFixture<AdvisorEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AdvisorEndpointsFactory _factory;

    public AdvisorEndpointsTests(AdvisorEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostExploration_Returns201_CreatesExplorationAndOpeningJob_AndAuditMetadataCarriesIdsOnly()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingAuditCount = audit.Recorded.Count;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/growth/explorations",
            new CreateExplorationRequest { Direction = "I want to learn astrophotography." });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"POST /api/growth/explorations response: {body}");
        var json = JsonDocument.Parse(body);
        var explorationId = json.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, explorationId);
        Assert.Equal(ExplorationStatuses.Working, json.RootElement.GetProperty("status").GetString());

        // Exactly one Pending AdvisorTurn Opening job was enqueued.
        ReestablishAmbientContext(user.Id);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var jobs = await dbContext.Jobs.AsNoTracking()
            .Where(j => j.AccountId == user.Id && j.Type == GrowthJobTypes.AdvisorTurn)
            .ToListAsync();
        Assert.Single(jobs);
        Assert.Equal(JobStatus.Pending, jobs[0].Status);
        Assert.Equal(0, jobs[0].AttemptCount);

        var payload = JsonSerializer.Deserialize<AdvisorTurnPayload>(jobs[0].PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(explorationId, payload!.ExplorationId);
        Assert.Equal(AdvisorTurnModes.Opening, payload.Mode);

        // Audit metadata is ids + counts only, never message text.
        var newAuditEntries = audit.Recorded.Skip(existingAuditCount).ToList();
        var auditRow = Assert.Single(newAuditEntries);
        Assert.Equal("exploration_created", auditRow.Action);
        Assert.Equal("Exploration", auditRow.ResourceType);
        Assert.Equal(explorationId.ToString(), auditRow.ResourceId);
        Assert.NotNull(auditRow.MetadataJson);
        Assert.DoesNotContain("astrophotography", auditRow.MetadataJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hasOpeningDirection", auditRow.MetadataJson!, StringComparison.Ordinal);
        Assert.Contains("true", auditRow.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostExploration_NonStudent_Returns403()
    {
        var caller = await SeedUserAsync(ActorTypes.Organization, "non-student-advisor@example.com");
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/growth/explorations",
            new CreateExplorationRequest { Direction = "anything" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostExploration_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/growth/explorations",
            new CreateExplorationRequest { Direction = "anything" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostExploration_TwentyFirstExploration_Returns409ExplorationLimitReached()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        // Seed MaxExplorationsPerStudent Idle explorations for this account.
        await SeedManyIdleExplorationsAsync(user.Id, 20);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/growth/explorations",
            new CreateExplorationRequest { Direction = "another direction" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("exploration_limit_reached", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PostMessages_EmptyContentAndEmptyAnswers_Returns400MessageRequired()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedIdleExplorationAsync(user.Id);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync(
            $"/api/growth/explorations/{explorationId}/messages",
            new AddExplorationMessageRequest());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("message_required", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PostMessages_ContentOver4000Characters_Returns400MessageTooLong()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedIdleExplorationAsync(user.Id);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var oversized = new string('x', 4_001);
        var response = await client.PostAsJsonAsync(
            $"/api/growth/explorations/{explorationId}/messages",
            new AddExplorationMessageRequest { Content = oversized });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("message_too_long", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PostMessages_WhileExplorationIsWorking_Returns409ExplorationBusy()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedExplorationAsync(user.Id, ExplorationStatuses.Working);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync(
            $"/api/growth/explorations/{explorationId}/messages",
            new AddExplorationMessageRequest { Content = "hi" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("exploration_busy", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task GetExploration_AfterRun_ReturnsAdvisorMessageWithQuestionsJson()
    {
        await ClearPendingJobsAsync();
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedExplorationAsync(user.Id, ExplorationStatuses.Working);
        await SeedAdvisorTurnJobAsync(user.Id, explorationId, AdvisorTurnModes.Opening);

        // Queue a deterministic advisor response: a reply + two questions.
        _factory.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: """{"reply":"Hi!","questions":[{"prompt":"Which topic?","options":["Math","Biology"]}]}""",
            ModelUsed: "test-model"));

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        // Drive the processor directly so we don't need to wait for the
        // background worker tick — the endpoint test only needs to see
        // the post-turn state.
        await RunOneAdvisorTickAsync(explorationId);

        var response = await client.GetAsync($"/api/growth/explorations/{explorationId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"GET /api/growth/explorations/{{id}} response: {body}");
        var json = JsonDocument.Parse(body);

        var messages = json.RootElement.GetProperty("messages");
        var advisorMsg = messages.EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == ExplorationRoles.Advisor);
        Assert.Equal("Hi!", advisorMsg.GetProperty("content").GetString());

        var questions = advisorMsg.GetProperty("questions");
        Assert.Equal(JsonValueKind.Array, questions.ValueKind);
        Assert.Equal(1, questions.GetArrayLength());
        var q = questions[0];
        Assert.Equal("Which topic?", q.GetProperty("prompt").GetString());
        Assert.Equal(new[] { "Math", "Biology" },
            q.GetProperty("options").EnumerateArray().Select(o => o.GetString()).ToArray());
    }

    [Fact]
    public async Task PostRefresh_WhileIdle_Returns202_AndEnqueuesPendingJob()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedIdleExplorationAsync(user.Id);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsync(
            $"/api/growth/explorations/{explorationId}/refresh", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        ReestablishAmbientContext(user.Id);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var jobs = await dbContext.Jobs.AsNoTracking()
            .Where(j => j.AccountId == user.Id && j.Type == GrowthJobTypes.AdvisorTurn)
            .ToListAsync();
        Assert.Single(jobs);
        Assert.Equal(AdvisorTurnModes.Refresh, JsonSerializer.Deserialize<AdvisorTurnPayload>(jobs[0].PayloadJson)!.Mode);
    }

    [Fact]
    public async Task PostRefresh_WhileWorking_Returns409ExplorationBusy()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedExplorationAsync(user.Id, ExplorationStatuses.Working);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsync(
            $"/api/growth/explorations/{explorationId}/refresh", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("exploration_busy", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PostRetry_WhileIdle_Returns409ExplorationNotRetryable()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedIdleExplorationAsync(user.Id);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsync(
            $"/api/growth/explorations/{explorationId}/retry", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("exploration_not_retryable", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PostRetry_AfterFailed_Returns202AndFlipsStatusToWorking()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedExplorationAsync(
            user.Id,
            ExplorationStatuses.Failed,
            lastError: "previous failure");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsync(
            $"/api/growth/explorations/{explorationId}/retry", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        ReestablishAmbientContext(user.Id);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var exploration = await dbContext.Explorations.AsNoTracking().SingleAsync(e => e.Id == explorationId);
        Assert.Equal(ExplorationStatuses.Working, exploration.Status);
        Assert.Null(exploration.LastError);
    }

    [Fact]
    public async Task PutTitle_EmptyTitle_Returns400TitleRequired()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedIdleExplorationAsync(user.Id);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var content = new StringContent(
            """{"title":""}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PutAsync(
            $"/api/growth/explorations/{explorationId}/title", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("title_required", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PutTitle_TitleOver200Characters_Returns400TitleTooLong()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedIdleExplorationAsync(user.Id);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var oversized = new string('t', 201);
        var content = new StringContent(
            JsonSerializer.Serialize(new { title = oversized }),
            Encoding.UTF8,
            "application/json");
        var response = await client.PutAsync(
            $"/api/growth/explorations/{explorationId}/title", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("title_too_long", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task DeleteExploration_Returns204_CascadesDependentRowsAndAuditMetadataCarriesCountsOnly()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingAuditCount = audit.Recorded.Count;

        var explorationId = await SeedIdleExplorationAsync(user.Id);

        // Seed dependent rows so the cascade delete has something to remove.
        ReestablishAmbientContext(user.Id);
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var exploration = await db.Explorations.AsNoTracking().SingleAsync(e => e.Id == explorationId);
            for (var i = 0; i < 3; i++)
            {
                db.ExplorationMessages.Add(new ExplorationMessage
                {
                    Id = Guid.NewGuid(),
                    AccountId = user.Id,
                    ExplorationId = explorationId,
                    Role = i % 2 == 0 ? ExplorationRoles.Student : ExplorationRoles.Advisor,
                    Content = i % 2 == 0 ? "hi student" : "hi advisor",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            db.ExplorationSummaryVersions.Add(new ExplorationSummaryVersion
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                ExplorationId = explorationId,
                VersionNumber = 1,
                GapsJson = "[]",
                SuggestionsJson = "[]",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.DeleteAsync($"/api/growth/explorations/{explorationId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        ReestablishAmbientContext(user.Id);
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<WriteDbContext>();
        Assert.Empty(await verifyContext.Explorations.Where(e => e.Id == explorationId).ToListAsync());
        Assert.Empty(await verifyContext.ExplorationMessages.Where(m => m.ExplorationId == explorationId).ToListAsync());
        Assert.Empty(await verifyContext.ExplorationSummaryVersions.Where(v => v.ExplorationId == explorationId).ToListAsync());

        var newAuditEntries = audit.Recorded.Skip(existingAuditCount).ToList();
        var auditRow = Assert.Single(newAuditEntries);
        Assert.Equal("exploration_deleted", auditRow.Action);
        Assert.Equal("Exploration", auditRow.ResourceType);
        Assert.Equal(explorationId.ToString(), auditRow.ResourceId);
        Assert.NotNull(auditRow.MetadataJson);
        Assert.DoesNotContain("hi student", auditRow.MetadataJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hi advisor", auditRow.MetadataJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("messageCount", auditRow.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossAccount_GetsReturns404_Not403()
    {
        var owner = await SeedStudentUserAsync();
        var caller = await SeedStudentUserAsync();
        var callerTokens = await IssueTokensAsync(caller);

        var explorationId = await SeedIdleExplorationAsync(owner.Id);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", callerTokens.AccessToken);

        // GET
        var get = await client.GetAsync($"/api/growth/explorations/{explorationId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        // POST refresh
        var refresh = await client.PostAsync($"/api/growth/explorations/{explorationId}/refresh", content: null);
        Assert.Equal(HttpStatusCode.NotFound, refresh.StatusCode);

        // POST messages
        var msg = await client.PostAsJsonAsync(
            $"/api/growth/explorations/{explorationId}/messages",
            new AddExplorationMessageRequest { Content = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, msg.StatusCode);

        // POST retry
        var retry = await client.PostAsync($"/api/growth/explorations/{explorationId}/retry", content: null);
        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);

        // PUT title
        var put = await client.PutAsync(
            $"/api/growth/explorations/{explorationId}/title",
            new StringContent("""{"title":"new"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        // DELETE — must be a no-op 404, never a write side-effect.
        var delete = await client.DeleteAsync($"/api/growth/explorations/{explorationId}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // Compare with the other account's exploration id → 404.
        var compare = await client.PostAsJsonAsync(
            "/api/growth/explorations/compare",
            new CreateExplorationComparisonRequest
            {
                FirstExplorationId = explorationId,
                SecondExplorationId = Guid.NewGuid(),
            });
        Assert.Equal(HttpStatusCode.NotFound, compare.StatusCode);
    }

    [Fact]
    public async Task PostCompare_SameIdTwice_Returns400CompareNeedsTwo()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var id = await SeedIdleExplorationAsync(user.Id);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/growth/explorations/compare",
            new CreateExplorationComparisonRequest
            {
                FirstExplorationId = id,
                SecondExplorationId = id,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("compare_needs_two", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PostCompare_ExplorationHasNoSummary_Returns409ExplorationHasNoSummary()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var first = await SeedIdleExplorationAsync(user.Id);
        var second = await SeedIdleExplorationAsync(user.Id);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/growth/explorations/compare",
            new CreateExplorationComparisonRequest
            {
                FirstExplorationId = first,
                SecondExplorationId = second,
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("exploration_has_no_summary", await ReadErrorCodeAsync(response));
    }

    // ----- test infrastructure -----

    private async Task<User> SeedStudentUserAsync() =>
        await SeedUserAsync(ActorTypes.Student, $"advisor-test-{Guid.NewGuid():N}@example.com");

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
        return await tokenService.IssueTokensAsync(user, "advisor-test", CancellationToken.None);
    }

    private async Task<Guid> SeedIdleExplorationAsync(Guid accountId)
    {
        return await SeedExplorationAsync(accountId, ExplorationStatuses.Idle);
    }

    private async Task<Guid> SeedExplorationAsync(
        Guid accountId,
        string status,
        string? lastError = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);

        var exploration = new Exploration
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = "seeded",
            Status = status,
            LastError = lastError,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Explorations.Add(exploration);
        await dbContext.SaveChangesAsync();
        return exploration.Id;
    }

    private async Task SeedManyIdleExplorationsAsync(Guid accountId, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);

        for (var i = 0; i < count; i++)
        {
            dbContext.Explorations.Add(new Exploration
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Title = $"seeded-{i}",
                Status = ExplorationStatuses.Idle,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Enqueue an <see cref="AdvisorTurnPayload"/> job for the supplied
    /// exploration so <see cref="RunOneAdvisorTickAsync"/> has something to
    /// claim. Tests that want to drive the processor directly (without the
    /// full POST pipeline) seed a Pending Job row instead of round-tripping
    /// through the endpoint.
    /// </summary>
    private async Task SeedAdvisorTurnJobAsync(Guid accountId, Guid explorationId, string mode)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);

        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = GrowthJobTypes.AdvisorTurn,
            PayloadJson = JsonSerializer.Serialize(
                new AdvisorTurnPayload(explorationId, mode)),
            Status = JobStatus.Pending,
            AttemptCount = 0,
            AccountId = accountId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task RunOneAdvisorTickAsync(Guid explorationId)
    {
        // Tests that drive the processor directly call
        // <see cref="ClearPendingJobsAsync"/> themselves before seeding
        // — we don't run a stale-job purge here because that would
        // delete the just-seeded Pending job this very tick is
        // expected to claim. The InMemory store is shared across the
        // fixture, so each test that ticks the processor is responsible
        // for clearing its predecessor's leftovers at the top of its
        // own body.
        using var scope = _factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<
            Storporate.Modules.StudentGrowthExperience.Advisor.AdvisorTurnJobProcessor>();
        await processor.TryProcessOneAsync(CancellationToken.None);
    }

    /// <summary>
    /// Drop every Pending / Running Job in the InMemory store. Tests
    /// call this at the top of their body so a sibling test's leftover
    /// can't be claimed by the processor instead of the row this test
    /// just seeded. Runs under a system scope so the EF global query
    /// filter (which short-circuits on IsAdministrator) sees every
    /// stale row regardless of which test seeded it.
    /// </summary>
    private async Task ClearPendingJobsAsync()
    {
        using var clearScope = _factory.Services.CreateScope();
        var accountScope = clearScope.ServiceProvider
            .GetRequiredService<IBackgroundAccountScope>();
        using (accountScope.BeginSystemScope())
        {
            var db = clearScope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var staleJobs = await db.Jobs
                .Where(j => j.Status == JobStatus.Pending
                    || j.Status == JobStatus.Running)
                .ToListAsync();
            if (staleJobs.Count > 0)
            {
                db.Jobs.RemoveRange(staleJobs);
                await db.SaveChangesAsync();
            }
        }
    }

    private void ReestablishAmbientContext(Guid userId)
    {
        var accountContext = _factory.Services.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(userId);
        accountContext.SetAccountId(userId);
        accountContext.SetIsAdministrator(false);
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
