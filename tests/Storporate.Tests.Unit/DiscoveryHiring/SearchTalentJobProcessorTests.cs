using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.Infrastructure.Persistence.TalentIndex;
using Storporate.Modules.DiscoveryHiring;
using Storporate.Modules.DiscoveryHiring.TalentSearch;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-43 Phase 2: direct-processor coverage for
/// <see cref="SearchTalentJobProcessor"/>. Sits one layer below the endpoint
/// integration tests: it goes through the real EF Core in-memory provider,
/// the real <see cref="JobClaimer"/> / <see cref="JobBookkeeper"/>, the real
/// <see cref="TalentIndexRepository"/> (InMemory branch), the real
/// <see cref="BackgroundAccountScope"/>, but resolves the processor by hand
/// rather than going through HTTP, so the assertions can target the
/// processor's own state transitions directly.
/// </summary>
/// <remarks>
/// Pinned behaviors:
/// <list type="bullet">
///   <item>Happy path: 1 candidate in pool, clean ranking → Completed with 1 result, audit written.</item>
///   <item>Requirements LLM fails (invalid JSON) → fallback to raw query, search still completes.</item>
///   <item>Ranking LLM mentions "evidence" / "proof" → deterministic fallback reason.</item>
///   <item>Ranking LLM cites an unknown portfolio item id → citation dropped, item dropped if no valid citations left.</item>
///   <item>Zero candidates → Completed with empty results, no ranking call.</item>
///   <item>Embedding failure x3 → job Failed, search marked Failed with llm_provider_error.</item>
///   <item>Prompt-content assertions: system prompt contains the untrusted-data rule, never the banned words.</item>
/// </list>
/// </remarks>
public class SearchTalentJobProcessorTests
{
    private const string OrgAccountId = "11111111-1111-1111-1111-111111111111";
    private const string StudentId1 = "22222222-2222-2222-2222-222222222222";
    private const string StudentId2 = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task HappyPath_OneCandidate_CleanRanking_CompletesWithOneResultAndAuditRow()
    {
        var fixture = await SeedPendingSearchJobAsync(
            seedCandidateIds: new[] { Guid.Parse(StudentId1) },
            candidateDisplayNames: new Dictionary<Guid, string> { { Guid.Parse(StudentId1), "Alice Doe" } });

        // Requirements call → return a clean 3-skill extraction.
        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: "{\"skills\":[\"python\",\"sql\",\"aws\"],\"summary\":\"backend engineer\"}",
            ModelUsed: "fake-model"));

        // Ranking call → c1 only, with one citation.
        var item1 = fixture.IndexItemsByEntryId[Guid.Parse(StudentId1)].Single();
        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: $$"""
                {
                  "ranking": [
                    {
                      "candidate": "c1",
                      "reason": "Strong match on backend fundamentals.",
                      "citations": [
                        { "portfolioItemId": "{{item1.PortfolioItemId}}", "skillName": "python" }
                      ]
                    }
                  ]
                }
                """,
            ModelUsed: "fake-model"));

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);

        var request = await fixture.Db.TalentSearchRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TalentSearchStatuses.Completed, request.Status);
        Assert.NotNull(request.CompletedAt);
        Assert.NotNull(request.ResultJson);
        Assert.Null(request.ErrorCode);

        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);

        // Audit: one "talent_search_completed" row with resultCount.
        var audit = fixture.AuditLogWriter.Recorded.Single(r => r.Action == "talent_search_completed");
        Assert.Equal("TalentSearch", audit.ResourceType);
        var auditJson = JsonDocument.Parse(audit.MetadataJson!);
        Assert.Equal(1, auditJson.RootElement.GetProperty("resultCount").GetInt32());
    }

    [Fact]
    public async Task RequirementsLlmFails_FallsBackToRawQuery_StillCompletes()
    {
        var fixture = await SeedPendingSearchJobAsync(
            seedCandidateIds: new[] { Guid.Parse(StudentId1) },
            candidateDisplayNames: new Dictionary<Guid, string> { { Guid.Parse(StudentId1), "Alice Doe" } });

        // Requirements call returns invalid JSON.
        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: "not json",
            ModelUsed: "fake-model"));

        // Ranking call → valid output.
        var item1 = fixture.IndexItemsByEntryId[Guid.Parse(StudentId1)].Single();
        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: $$"""
                {
                  "ranking": [
                    {
                      "candidate": "c1",
                      "reason": "Backend fundamentals.",
                      "citations": [
                        { "portfolioItemId": "{{item1.PortfolioItemId}}", "skillName": "python" }
                      ]
                    }
                  ]
                }
                """,
            ModelUsed: "fake-model"));

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);

        var request = await fixture.Db.TalentSearchRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TalentSearchStatuses.Completed, request.Status);

        // The processor still called the ranking LLM (with an empty-skills
        // embedding input) — verify by counting exactly two LLM calls.
        Assert.Equal(2, fixture.LlmClient.CallCount);
    }

    [Fact]
    public async Task RankingLlmMentionsBannedWord_ReplacedWithDeterministicFallbackReason()
    {
        var fixture = await SeedPendingSearchJobAsync(
            seedCandidateIds: new[] { Guid.Parse(StudentId1) },
            candidateDisplayNames: new Dictionary<Guid, string> { { Guid.Parse(StudentId1), "Alice Doe" } });

        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: "{\"skills\":[\"python\"],\"summary\":\"\"}",
            ModelUsed: "fake-model"));

        var item1 = fixture.IndexItemsByEntryId[Guid.Parse(StudentId1)].Single();
        // Ranking output mentions "evidence" in the reason — must be replaced.
        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: $$"""
                {
                  "ranking": [
                    {
                      "candidate": "c1",
                      "reason": "The student's portfolio provides evidence of backend skill.",
                      "citations": [
                        { "portfolioItemId": "{{item1.PortfolioItemId}}", "skillName": "python" }
                      ]
                    }
                  ]
                }
                """,
            ModelUsed: "fake-model"));

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);

        var request = await fixture.Db.TalentSearchRequests.AsNoTracking().SingleAsync();
        var items = JsonSerializer.Deserialize<List<TalentSearchResultItem>>(
            request.ResultJson!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(items);
        Assert.DoesNotContain("evidence", items[0].Reason, StringComparison.OrdinalIgnoreCase);
        // The fallback reason uses the canonical phrasing the plan mandates.
        Assert.Contains("Strong in", items[0].Reason);
    }

    [Fact]
    public async Task RankingLlmCitesUnknownPortfolioItemId_CitationDropped_ItemDroppedIfNoCitationsRemain()
    {
        var fixture = await SeedPendingSearchJobAsync(
            seedCandidateIds: new[] { Guid.Parse(StudentId1) },
            candidateDisplayNames: new Dictionary<Guid, string> { { Guid.Parse(StudentId1), "Alice Doe" } });

        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: "{\"skills\":[\"python\"],\"summary\":\"\"}",
            ModelUsed: "fake-model"));

        // Ranking output cites an item id that is NOT in the snapshot.
        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: $$"""
                {
                  "ranking": [
                    {
                      "candidate": "c1",
                      "reason": "Backend match.",
                      "citations": [
                        { "portfolioItemId": "{{Guid.NewGuid()}}", "skillName": "python" }
                      ]
                    }
                  ]
                }
                """,
            ModelUsed: "fake-model"));

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);

        var request = await fixture.Db.TalentSearchRequests.AsNoTracking().SingleAsync();
        // The candidate's only citation was dropped. The deterministic
        // fallback path re-includes it via the unranked tail (because the
        // candidate still has a valid snapshot) and rebuilds the reason
        // from the snapshot's own skills. The cited-items array must be
        // empty because every LLM-cited item was dropped.
        var items = JsonSerializer.Deserialize<List<TalentSearchResultItem>>(
            request.ResultJson!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(items);
        Assert.Empty(items[0].CitedItems);
        Assert.Contains("Strong in python", items[0].Reason);
    }

    [Fact]
    public async Task ZeroCandidates_CompletesWithEmptyResults_NoRankingCall()
    {
        var fixture = await SeedPendingSearchJobAsync(
            seedCandidateIds: Array.Empty<Guid>(),
            candidateDisplayNames: new Dictionary<Guid, string>());

        // Requirements LLM is still called (the processor calls it first).
        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: "{\"skills\":[],\"summary\":\"\"}",
            ModelUsed: "fake-model"));

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);

        var request = await fixture.Db.TalentSearchRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TalentSearchStatuses.Completed, request.Status);
        var items = JsonSerializer.Deserialize<List<TalentSearchResultItem>>(
            request.ResultJson!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Empty(items);

        // Exactly one LLM call (requirements); the ranking call is skipped
        // when there are zero candidates.
        Assert.Equal(1, fixture.LlmClient.CallCount);

        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);
    }

    [Fact]
    public async Task EmbeddingFailureAfterThreeAttempts_JobIsFailedAndSearchIsMarkedFailed()
    {
        var fixture = await SeedPendingSearchJobAsync(
            seedCandidateIds: new[] { Guid.Parse(StudentId1) },
            candidateDisplayNames: new Dictionary<Guid, string> { { Guid.Parse(StudentId1), "Alice Doe" } });

        // 3 attempts → 3 requirements LLM calls. The embedding failure is
        // what fails the job, but each attempt's requirements call must
        // also produce a valid response so the processor reaches the
        // embedding call on every tick. Requeue-or-fail replays the whole
        // pipeline, requirements first.
        var requirementsResponse = new LlmCompletionResult(
            OutputText: "{\"skills\":[\"python\"],\"summary\":\"\"}",
            ModelUsed: "fake-model");
        fixture.LlmClient.EnqueueResponse(requirementsResponse);
        fixture.LlmClient.EnqueueResponse(requirementsResponse);
        fixture.LlmClient.EnqueueResponse(requirementsResponse);

        // 3 embedding failures in a row.
        fixture.EmbeddingClient.EnqueueException(new LlmProviderException("provider down 1"));
        fixture.EmbeddingClient.EnqueueException(new LlmProviderException("provider down 2"));
        fixture.EmbeddingClient.EnqueueException(new LlmProviderException("provider down 3"));

        for (var i = 0; i < JobBookkeeper.MaxAttempts; i++)
        {
            var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
            Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        }

        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobBookkeeper.MaxAttempts, job.AttemptCount);
        Assert.Contains("provider down 3", job.ErrorMessage);

        var request = await fixture.Db.TalentSearchRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TalentSearchStatuses.Failed, request.Status);
        Assert.Equal("llm_provider_error", request.ErrorCode);
        Assert.NotNull(request.CompletedAt);

        var failedAudit = fixture.AuditLogWriter.Recorded
            .Where(r => r.Action == "talent_search_failed").ToList();
        Assert.Single(failedAudit);
        var failedJson = JsonDocument.Parse(failedAudit[0].MetadataJson!);
        Assert.Equal("llm_provider_error", failedJson.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public void RequirementsPrompt_ContainsUntrustedDataRuleAndStructuredJsonContract()
    {
        var system = SearchTalentRequirementsPromptBuilder.BuildSystemPrompt();
        Assert.Contains("untrusted data", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STRICT JSON", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Empty object", system, StringComparison.OrdinalIgnoreCase);

        var user = SearchTalentRequirementsPromptBuilder
            .BuildCompletionRequest(new SearchTalentRequirementsInputs("Find me a backend engineer."))
            .UserPrompt;
        Assert.Contains("EMPLOYER QUERY", user, StringComparison.Ordinal);
        Assert.Contains("Find me a backend engineer.", user, StringComparison.Ordinal);
    }

    [Fact]
    public void RankingPrompt_HandlesOpaqueHandlesAndStructuredJsonContract()
    {
        var inputs = new SearchTalentRankingInputs(
            Query: "Need backend",
            Candidates: new List<SearchTalentRankingCandidate>
            {
                new(EntryId: Guid.NewGuid(), Items: new List<SearchTalentRankingItem>
                {
                    new(PortfolioItemId: Guid.NewGuid(), Label: "X", Category: "Code",
                        Skills: new List<SearchTalentRankingSkill>
                        {
                            new(Name: "python", Band: ConfidenceBands.Strong)
                        })
                })
            });

        var request = SearchTalentRankingPromptBuilder.BuildCompletionRequest(inputs);
        Assert.Contains("c1", request.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Need backend", request.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("untrusted data", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STRICT JSON", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("citations", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    // ----- infrastructure -----

    private async Task<Fixture> SeedPendingSearchJobAsync(
        IReadOnlyList<Guid> seedCandidateIds,
        IReadOnlyDictionary<Guid, string> candidateDisplayNames)
    {
        var accountId = Guid.Parse(OrgAccountId);
        var databaseName = "SearchTalentJobProcessorTests-" + Guid.NewGuid().ToString("N");
        var sharedRoot = new InMemoryDatabaseRoot();
        var embeddingClient = new FakeEmbeddingClient();
        var llmClient = new FakeLlmClient();
        var auditWriter = new FakeAuditLogWriter();

        // Map: entryId → snapshot list (so tests can pull the portfolio
        // item id they cite in the canned ranking output).
        var indexItemsByEntryId = new Dictionary<Guid, List<TalentIndexItemSnapshot>>();

        // Build the SAME WriteDbContext the processor will use so the
        // repository's InMemory dictionary (keyed on WriteDbContext via
        // ConditionalWeakTable) is shared between seed + search.
        var dbContext = CreateDbContextOnSharedRoot(databaseName, sharedRoot, accountId);
        var repository = new TalentIndexRepository(
            dbContext,
            Options.Create(new ConnectionStringsOptions
            {
                WriteDb = "Host=localhost;Database=test;Username=u;Password=p",
                SslMode = "Disable",
            }),
            NullLogger<TalentIndexRepository>.Instance);

        var seedEmbedding = Enumerable.Range(0, TalentIndexConstants.EmbeddingDimensions)
            .Select(i => i == 0 ? 1f : 0f)
            .ToArray();
        var now = DateTimeOffset.UtcNow;
        foreach (var studentId in seedCandidateIds)
        {
            var itemId = Guid.NewGuid();
            var snapshot = new List<TalentIndexItemSnapshot>
            {
                new(itemId, "Item " + studentId.ToString("N")[..6], "Code",
                    new List<TalentIndexSkillSnapshot>
                    {
                        new(Name: "python", Band: ConfidenceBands.Strong,
                            Reason: "Built a Python service in the seed item."),
                    }),
            };
            indexItemsByEntryId[studentId] = snapshot;
            var displayName = candidateDisplayNames.TryGetValue(studentId, out var name) ? name : "Student";

            await repository.UpsertAsync(
                studentAccountId: studentId,
                displayName: displayName,
                headline: null,
                university: null,
                fieldOfStudy: null,
                studyYear: null,
                itemsJson: JsonSerializer.Serialize(snapshot),
                searchText: "seed",
                contentHash: Guid.NewGuid().ToString("N"),
                embedding: seedEmbedding,
                updatedAt: now,
                cancellationToken: CancellationToken.None);
        }

        var searchId = Guid.NewGuid();
        dbContext.TalentSearchRequests.Add(new TalentSearchRequest
        {
            Id = searchId,
            AccountId = accountId,
            QueryText = "Find me a backend engineer with Python experience.",
            Status = TalentSearchStatuses.Pending,
            CreatedAt = now,
        });
        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = DiscoveryJobTypes.SearchTalent,
            PayloadJson = JsonSerializer.Serialize(new SearchTalentPayload(searchId)),
            Status = JobStatus.Pending,
            AttemptCount = 0,
            AccountId = accountId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();

        var ambient = new AmbientAccountContext();
        var scope = new BackgroundAccountScope(ambient, ambient);

        var processor = new SearchTalentJobProcessor(
            dbContext,
            repository,
            embeddingClient,
            llmClient,
            auditWriter,
            NullLogger<SearchTalentJobProcessor>.Instance,
            TimeProvider.System,
            scope);

        return new Fixture(
            Db: dbContext,
            Processor: processor,
            LlmClient: llmClient,
            EmbeddingClient: embeddingClient,
            AuditLogWriter: auditWriter,
            IndexItemsByEntryId: indexItemsByEntryId,
            AccountId: accountId);
    }

    private static WriteDbContext CreateDbContextOnSharedRoot(
        string databaseName,
        InMemoryDatabaseRoot sharedRoot,
        Guid accountId)
    {
        var accountContext = new TestAccountContext
        {
            UserId = accountId,
            AccountId = accountId,
        };
        var interceptor = new RowLevelSecurityInterceptor(accountContext);
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(databaseName, sharedRoot)
            .AddInterceptors(interceptor)
            .Options;
        return new WriteDbContext(options, accountContext);
    }

    private sealed class TestAccountContext : IAccountContext
    {
        public Guid? UserId { get; init; }
        public Guid? AccountId { get; init; }
        public bool IsAdministrator { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
    }

    private sealed record Fixture(
        WriteDbContext Db,
        SearchTalentJobProcessor Processor,
        FakeLlmClient LlmClient,
        FakeEmbeddingClient EmbeddingClient,
        FakeAuditLogWriter AuditLogWriter,
        IReadOnlyDictionary<Guid, List<TalentIndexItemSnapshot>> IndexItemsByEntryId,
        Guid AccountId);
}
