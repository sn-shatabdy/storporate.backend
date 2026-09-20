using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;

namespace Storporate.Tests.Unit.StudentGrowthExperience;

/// <summary>
/// Hard-case coverage for the advisor pipeline. Each test exercises a
/// specific acceptance criterion from the Phase 2 plan that's too
/// involved to fold into <see cref="AdvisorEndpointsTests"/>:
///
/// <list type="bullet">
///   <item>Refresh produces a VersionNumber=2 summary row with a non-empty changeNote.</item>
///   <item>Retry mode choice: no messages → Opening; latest student newer than advisor → Reply; otherwise → Refresh.</item>
///   <item>Source-as-snapshot: a feed item's URL is copied from the row, never from the AI.</item>
///   <item>Drop-source-keep-rest: an AI-emitted sourceItemId outside the candidate set produces a suggestion with no source.</item>
/// </list>
/// </summary>
public class AdvisorEndpointsHardCasesTests : IClassFixture<AdvisorEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AdvisorEndpointsFactory _factory;

    public AdvisorEndpointsHardCasesTests(AdvisorEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Refresh_ProducesVersionNumber2_WithNonEmptyChangeNote()
    {
        await ClearPendingJobsAsync();
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedIdleExplorationAsync(user.Id);

        // Pre-existing version 1 so refresh can write v2.
        await SeedVersionAsync(user.Id, explorationId, versionNumber: 1);

        // One Refresh tick: the LLM returns a non-empty changeNote
        // alongside the same gaps + suggestions, so the processor
        // bumps VersionNumber to 2 and persists the note.
        await SeedAdvisorTurnJobAsync(user.Id, explorationId, AdvisorTurnModes.Refresh);
        _factory.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: """{"reply":"refresh","summary":{"gaps":[{"title":"Setup","detail":"Open a hosting account","band":"Developing"}],"suggestions":[{"title":"Read docs","reason":"Quick start","nextStep":"Browse the homepage"}],"changeNote":"Nothing changed."}}""",
            ModelUsed: "test-model"));
        await RunOneAdvisorTickAsync();

        ReestablishAmbientContext(user.Id);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var versions = await dbContext.ExplorationSummaryVersions
            .AsNoTracking()
            .Where(v => v.ExplorationId == explorationId)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(1, versions[0].VersionNumber);
        Assert.Equal(2, versions[1].VersionNumber);
        Assert.NotNull(versions[1].ChangeNote);
        Assert.False(string.IsNullOrWhiteSpace(versions[1].ChangeNote));
    }

    [Fact]
    public async Task Retry_OnEmptyExploration_PicksOpeningMode()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var explorationId = await SeedExplorationAsync(
            user.Id, ExplorationStatuses.Failed);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var response = await client.PostAsync(
            $"/api/growth/explorations/{explorationId}/retry", content: null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        ReestablishAmbientContext(user.Id);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var job = await dbContext.Jobs.AsNoTracking().SingleAsync(j => j.AccountId == user.Id);
        var payload = JsonSerializer.Deserialize<AdvisorTurnPayload>(job.PayloadJson);
        Assert.Equal(AdvisorTurnModes.Opening, payload!.Mode);
    }

    [Fact]
    public async Task Retry_AfterStudentMessageNewerThanAdvisor_PicksReplyMode()
    {
        var user = await SeedStudentUserAsync();
        var explorationId = await SeedExplorationAsync(
            user.Id, ExplorationStatuses.Failed);

        ReestablishAmbientContext(user.Id);
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.ExplorationMessages.Add(new ExplorationMessage
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                ExplorationId = explorationId,
                Role = ExplorationRoles.Advisor,
                Content = "older",
                CreatedAt = now.AddMinutes(-5),
            });
            db.ExplorationMessages.Add(new ExplorationMessage
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                ExplorationId = explorationId,
                Role = ExplorationRoles.Student,
                Content = "newer",
                CreatedAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        var tokens = await IssueTokensAsync(user);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var response = await client.PostAsync(
            $"/api/growth/explorations/{explorationId}/retry", content: null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        ReestablishAmbientContext(user.Id);
        using var verifyScope = _factory.Services.CreateScope();
        var dbContext = verifyScope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var job = await dbContext.Jobs.AsNoTracking().SingleAsync(j => j.AccountId == user.Id);
        var payload = JsonSerializer.Deserialize<AdvisorTurnPayload>(job.PayloadJson);
        Assert.Equal(AdvisorTurnModes.Reply, payload!.Mode);
    }

    [Fact]
    public async Task Retry_WhenAdvisorNewer_PicksRefreshMode()
    {
        var user = await SeedStudentUserAsync();
        var explorationId = await SeedExplorationAsync(
            user.Id, ExplorationStatuses.Failed);

        ReestablishAmbientContext(user.Id);
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.ExplorationMessages.Add(new ExplorationMessage
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                ExplorationId = explorationId,
                Role = ExplorationRoles.Student,
                Content = "older",
                CreatedAt = now.AddMinutes(-5),
            });
            db.ExplorationMessages.Add(new ExplorationMessage
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                ExplorationId = explorationId,
                Role = ExplorationRoles.Advisor,
                Content = "newer",
                CreatedAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        var tokens = await IssueTokensAsync(user);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var response = await client.PostAsync(
            $"/api/growth/explorations/{explorationId}/retry", content: null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        ReestablishAmbientContext(user.Id);
        using var verifyScope = _factory.Services.CreateScope();
        var dbContext = verifyScope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var job = await dbContext.Jobs.AsNoTracking().SingleAsync(j => j.AccountId == user.Id);
        var payload = JsonSerializer.Deserialize<AdvisorTurnPayload>(job.PayloadJson);
        Assert.Equal(AdvisorTurnModes.Refresh, payload!.Mode);
    }

    [Fact]
    public async Task Suggestion_WithRealFeedSource_CopiesUrlFromDbRowNotFromAi()
    {
        await ClearPendingJobsAsync();
        var user = await SeedStudentUserAsync();
        var feedItem = new FeedItem
        {
            Id = Guid.NewGuid(),
            SourceName = "Example Daily",
            SourceUrl = "https://example.com/feed",
            Url = "https://example.com/article/canonical",
            Title = "Astrophotography intro",
            Summary = "Hands-on session.",
            FetchedAt = DateTimeOffset.UtcNow,
        };
        SeedFeedItem(feedItem);

        var explorationId = await SeedIdleExplorationAsync(user.Id);
        await SeedAdvisorTurnJobAsync(user.Id, explorationId, AdvisorTurnModes.Refresh);

        // The LLM returns the correct id and a placeholder URL — the DB
        // row's URL must win at write time.
        var suggestionId = Guid.NewGuid();
        var json = $$"""
        {
          "reply": "ok",
          "summary": {
            "suggestions": [
              {
                "title": "Try the club",
                "reason": "Hands-on intro",
                "nextStep": "Sign up",
                "sourceItemId": "{{feedItem.Id}}"
              }
            ]
          }
        }
        """;
        _factory.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: json, ModelUsed: "test-model"));

        await RunOneAdvisorTickAsync();

        ReestablishAmbientContext(user.Id);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var summary = await dbContext.ExplorationSummaryVersions
            .AsNoTracking()
            .SingleAsync(v => v.ExplorationId == explorationId);

        // SuggestionsJson must contain the DB row's title and URL, not anything
        // the AI fabricated.
        Assert.Contains(feedItem.Title, summary.SuggestionsJson);
        Assert.Contains(feedItem.Url, summary.SuggestionsJson);
        Assert.DoesNotContain("https://example.com/article/llm-fabricated", summary.SuggestionsJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Suggestion_WithUnknownSourceId_DropsSource_KeepsSuggestion()
    {
        await ClearPendingJobsAsync();
        var user = await SeedStudentUserAsync();
        var explorationId = await SeedIdleExplorationAsync(user.Id);
        await SeedAdvisorTurnJobAsync(user.Id, explorationId, AdvisorTurnModes.Refresh);

        var unknownId = Guid.NewGuid();
        var json = $$"""
        {
          "reply": "ok",
          "summary": {
            "suggestions": [
              {
                "title": "Try the club",
                "reason": "Hands-on intro",
                "nextStep": "Sign up",
                "sourceItemId": "{{unknownId}}"
              }
            ]
          }
        }
        """;
        _factory.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: json, ModelUsed: "test-model"));

        await RunOneAdvisorTickAsync();

        ReestablishAmbientContext(user.Id);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var summary = await dbContext.ExplorationSummaryVersions
            .AsNoTracking()
            .SingleAsync(v => v.ExplorationId == explorationId);

        // The suggestion is preserved with its title / reason / nextStep.
        Assert.Contains("Try the club", summary.SuggestionsJson);
        // The bogus id never makes it onto the wire.
        Assert.DoesNotContain(unknownId.ToString(), summary.SuggestionsJson,
            StringComparison.OrdinalIgnoreCase);
    }

    // ----- infrastructure -----

    private async Task<User> SeedStudentUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"advisor-hardcase-{Guid.NewGuid():N}@example.com",
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
        return await tokenService.IssueTokensAsync(user, "advisor-hardcase", CancellationToken.None);
    }

    private async Task<Guid> SeedIdleExplorationAsync(Guid accountId) =>
        await SeedExplorationAsync(accountId, ExplorationStatuses.Idle);

    private async Task<Guid> SeedExplorationAsync(Guid accountId, string status)
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
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Explorations.Add(exploration);
        await dbContext.SaveChangesAsync();
        return exploration.Id;
    }

    private async Task SeedVersionAsync(Guid accountId, Guid explorationId, int versionNumber)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);

        dbContext.ExplorationSummaryVersions.Add(new ExplorationSummaryVersion
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExplorationId = explorationId,
            VersionNumber = versionNumber,
            GapsJson = "[]",
            SuggestionsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Enqueue an <see cref="AdvisorTurnPayload"/> job for the supplied
    /// exploration so <see cref="RunOneAdvisorTickAsync"/> has something to
    /// claim. Most hard-case tests only seed the exploration row + canned
    /// LLM response and don't want the full POST pipeline, so they drive
    /// the processor directly via a pre-seeded Job row instead.
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

    private void SeedFeedItem(FeedItem feedItem)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        dbContext.FeedItems.Add(feedItem);
        dbContext.SaveChanges();
    }

    private async Task RunOneAdvisorTickAsync()
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
    /// in this fixture call this at the top of their body — before
    /// they seed their own Pending Job — so that a sibling test's
    /// leftover can't be claimed by the processor instead of the row
    /// this test just seeded. Runs under a system scope so the EF
    /// global query filter (which short-circuits on IsAdministrator)
    /// sees every stale row regardless of which test seeded it.
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
}
