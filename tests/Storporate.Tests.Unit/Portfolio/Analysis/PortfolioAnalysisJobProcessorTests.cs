using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.Modules.Portfolio.Analysis;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Storage;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Portfolio.Analysis;

/// <summary>
/// TDD coverage for <see cref="PortfolioAnalysisJobProcessor"/>. Each test
/// stands up a real <see cref="WriteDbContext"/> against the in-memory EF
/// provider with a shared database root so seed and processor can read each
/// other's writes, plus a <see cref="FakeLlmClient"/> whose response queue the
/// test controls — this is the same fake pattern as the existing
/// <see cref="FakeArtifactStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The five tests pin the Phase 2 acceptance criteria from the plan:
/// successful analysis (findings written, job Succeeded, item Analyzed),
/// retry-then-fail after 3 attempts (job Failed, item Failed, ErrorMessage
/// populated), unsupported short-circuit (no LLM call), link-type prompt
/// content (Label/Description in the prompt, no file extraction attempted),
/// and concurrent-tick row-claim (two ticks, one Succeeded outcome).
/// </para>
/// </remarks>
public class PortfolioAnalysisJobProcessorTests
{
    [Fact]
    public async Task TryProcessOneAsync_SeededPendingJobWithValidLlmResponse_SucceedsAndWritesFindings()
    {
        var (dbContext, accountId, portfolioItemId) = await SeedPendingJobAsync(
            submissionType: PortfolioSubmissionTypes.Link,
            category: PortfolioCategories.PortfolioLink);

        var llmClient = new FakeLlmClient();
        llmClient.EnqueueResponse(SkillsJson(
            ("React", ConfidenceBands.Strong, "Builds a component-based UI in the description."),
            ("Public Speaking", ConfidenceBands.Developing, "Mentions a class presentation but no transcript."),
            ("TypeScript", ConfidenceBands.Missing, "No TypeScript code or typescript evidence in the text.")));

        var (processor, _) = CreateProcessor(dbContext, llmClient);
        var outcome = await processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);

        var job = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.NotNull(job.CompletedAt);
        Assert.Null(job.ErrorMessage);

        var item = await dbContext.PortfolioItems.AsNoTracking().SingleAsync();
        Assert.Equal(PortfolioAnalysisStatuses.Analyzed, item.AnalysisStatus);
        Assert.NotNull(item.LastAnalyzedAt);

        var findings = await dbContext.PortfolioSkillFindings.AsNoTracking().ToListAsync();
        Assert.Equal(3, findings.Count);
        Assert.All(findings, finding => Assert.Equal(accountId, finding.AccountId));
        Assert.All(findings, finding => Assert.Equal(portfolioItemId, finding.PortfolioItemId));
        Assert.Contains(findings, f => f.SkillName == "React" && f.ConfidenceBand == ConfidenceBands.Strong);
        Assert.Contains(findings, f => f.SkillName == "Public Speaking" && f.ConfidenceBand == ConfidenceBands.Developing);
        Assert.Contains(findings, f => f.SkillName == "TypeScript" && f.ConfidenceBand == ConfidenceBands.Missing);

        Assert.Equal(1, llmClient.CallCount);
    }

    [Fact]
    public async Task TryProcessOneAsync_LlmAlwaysThrowsAfterThreeAttempts_JobFailsAndItemFails()
    {
        var (dbContext, _, _) = await SeedPendingJobAsync(
            submissionType: PortfolioSubmissionTypes.Link,
            category: PortfolioCategories.PortfolioLink);

        var llmClient = new FakeLlmClient();
        // Queue three throws: MaxAttempts is 3, so attempt 1 throws -> re-queue
        // Pending, attempt 2 throws -> re-queue Pending, attempt 3 throws ->
        // Failed. The fourth call would happen if the processor miscounted;
        // the test would still pass on the assertion below, but we don't need
        // a fourth canned throw to keep the assertion honest.
        llmClient.EnqueueException(new LlmProviderException("provider unreachable 1"));
        llmClient.EnqueueException(new LlmProviderException("provider unreachable 2"));
        llmClient.EnqueueException(new LlmProviderException("provider unreachable 3"));

        var (processor, _) = CreateProcessor(dbContext, llmClient);

        // Three ticks: the first two re-queue (Status = Pending, AttemptCount =
        // 1 and 2 respectively); the third exhausts the budget and flips to
        // Status = Failed. Run them serially against the same in-memory
        // database so the row-claim UPDATE finds the re-queued job.
        for (var i = 0; i < PortfolioAnalysisJobProcessor.MaxAttempts; i++)
        {
            var outcome = await processor.TryProcessOneAsync(CancellationToken.None);
            Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        }

        var job = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(PortfolioAnalysisJobProcessor.MaxAttempts, job.AttemptCount);
        Assert.NotNull(job.ErrorMessage);
        Assert.NotEmpty(job.ErrorMessage);
        Assert.Contains("LLM provider error", job.ErrorMessage);

        var item = await dbContext.PortfolioItems.AsNoTracking().SingleAsync();
        Assert.Equal(PortfolioAnalysisStatuses.Failed, item.AnalysisStatus);
        Assert.Null(item.LastAnalyzedAt);

        Assert.Equal(PortfolioAnalysisJobProcessor.MaxAttempts, llmClient.CallCount);

        var findings = await dbContext.PortfolioSkillFindings.AsNoTracking().ToListAsync();
        Assert.Empty(findings);
    }

    [Fact]
    public async Task TryProcessOneAsync_VideoSubmission_ShortCircuitsToUnsupportedWithoutCallingLlm()
    {
        var (dbContext, _, _) = await SeedPendingJobAsync(
            submissionType: PortfolioSubmissionTypes.File,
            category: PortfolioCategories.Video,
            storageKey: "portfolio/abc/video.mp4",
            contentType: "video/mp4");

        var llmClient = new FakeLlmClient();

        var (processor, _) = CreateProcessor(dbContext, llmClient);
        var outcome = await processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);

        var job = await dbContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);

        var item = await dbContext.PortfolioItems.AsNoTracking().SingleAsync();
        Assert.Equal(PortfolioAnalysisStatuses.Unsupported, item.AnalysisStatus);
        Assert.Null(item.LastAnalyzedAt);

        // The headline assertion: zero LLM calls. The whole reason this category
        // is on the unsupported list is so the worker doesn't waste an LLM
        // round-trip on a video file it can't see.
        Assert.Equal(0, llmClient.CallCount);
    }

    [Fact]
    public async Task TryProcessOneAsync_LinkSubmission_PromptContainsLabelAndDescriptionAndNotFileBytes()
    {
        var (dbContext, _, _) = await SeedPendingJobAsync(
            submissionType: PortfolioSubmissionTypes.Link,
            category: PortfolioCategories.PortfolioLink,
            label: "Senior capstone",
            description: "Built a Next.js + GraphQL dashboard analyzing local election turnout.");

        var llmClient = new FakeLlmClient();
        llmClient.EnqueueResponse(SkillsJson(
            ("Next.js", ConfidenceBands.Strong, "Next.js mentioned by name and used in the dashboard build."),
            ("GraphQL", ConfidenceBands.Strong, "GraphQL schema/query usage is named explicitly.")));

        var (processor, _) = CreateProcessor(dbContext, llmClient);
        await processor.TryProcessOneAsync(CancellationToken.None);

        var call = Assert.Single(llmClient.Calls);

        // The user prompt must carry the student's own label/description and
        // category — those are the only signals the worker has for a link-type
        // submission (per the plan's "no external URL fetch" hard constraint).
        Assert.Contains("Senior capstone", call.UserPrompt);
        Assert.Contains("Built a Next.js + GraphQL dashboard analyzing local election turnout.", call.UserPrompt);
        Assert.Contains(PortfolioCategories.PortfolioLink, call.UserPrompt);

        // The system prompt is the strict-JSON contract that gates parsing.
        Assert.NotNull(call.SystemPrompt);
        Assert.Contains("Strong", call.SystemPrompt);
        Assert.Contains("Developing", call.SystemPrompt);
        Assert.Contains("Missing", call.SystemPrompt);
        Assert.Contains("JSON", call.SystemPrompt);

        // The prompt must NOT carry any file-extraction-shaped artifact (no
        // "PDF" / "DOCX" / "Extracted text:" framing — those are file-type
        // artifacts the extractor would inject, not link-type artifacts).
        Assert.DoesNotContain("Extracted text", call.UserPrompt);
        Assert.DoesNotContain(".pdf", call.UserPrompt);
        Assert.DoesNotContain(".docx", call.UserPrompt);

        // Print the captured prompt so a reviewer can spot-check it against
        // the plan's "link-type prompt content" assertion. The Console
        // output is informational, not asserted-on, and is captured in the
        // verification report.
        Console.WriteLine("--- Link-type user prompt captured by fake client ---");
        Console.WriteLine(call.UserPrompt);
        Console.WriteLine("--- end user prompt ---");
    }

    [Fact]
    public async Task TryProcessOneAsync_ConcurrentTicks_OnlyOneProcessesTheJob()
    {
        // Seed one Pending job. Two competing ticks (each on its own scope /
        // DbContext instance but sharing the same in-memory database root) both
        // race to claim it. The atomic UPDATE ... WHERE Status = 'Pending'
        // (Postgres) and the load-then-recheck-with-lock (InMemory provider)
        // ensure exactly one tick observes the claim succeed; the other either
        // sees NoWork (the row already moved off Pending) or ClaimedByAnother
        // (a concurrent claim raced us). Either outcome is acceptable for the
        // test — what matters is that the LLM was called exactly once and
        // exactly one finding row was written.
        var (_, accountId, portfolioItemId) = await SeedPendingJobAsync(
            submissionType: PortfolioSubmissionTypes.Link,
            category: PortfolioCategories.PortfolioLink);

        var databaseName = SharedPendingJobDatabase;
        var sharedRoot = SharedPendingJobRoot;

        var llmClient1 = new FakeLlmClient();
        llmClient1.EnqueueResponse(SkillsJson(("Skill A", ConfidenceBands.Strong, "explanation A")));
        var llmClient2 = new FakeLlmClient();
        llmClient2.EnqueueResponse(SkillsJson(("Skill B", ConfidenceBands.Strong, "explanation B")));

        var (processor1, _) = CreateProcessor(
            CreateDbContextOnSharedRoot(databaseName, sharedRoot, accountId),
            llmClient1);
        var (processor2, _) = CreateProcessor(
            CreateDbContextOnSharedRoot(databaseName, sharedRoot, accountId),
            llmClient2);

        // Fire both ticks concurrently; the test's value is in the post-state
        // assertions, not the await ordering.
        var task1 = Task.Run(async () => await processor1.TryProcessOneAsync(CancellationToken.None));
        var task2 = Task.Run(async () => await processor2.TryProcessOneAsync(CancellationToken.None));
        await Task.WhenAll(task1, task2);

        // The headline acceptance: the LLM was called exactly once across both
        // fake clients. If both processors ran end-to-end (no row-claim), the
        // total call count would be 2 and the findings table would have two
        // rows — both of which the assertions below pin.
        Assert.Equal(1, llmClient1.CallCount + llmClient2.CallCount);

        // Sanity-check the post-state from a fresh read against the shared
        // store: the job reached Succeeded exactly once, no duplicate findings.
        using var verifyContext = CreateDbContextOnSharedRoot(databaseName, sharedRoot, accountId);
        var job = await verifyContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);

        var findings = await verifyContext.PortfolioSkillFindings.AsNoTracking()
            .Where(f => f.PortfolioItemId == portfolioItemId)
            .ToListAsync();
        Assert.Single(findings);
    }

    // ----- Test infrastructure below this line -----

    /// <summary>Builds a skills-JSON <see cref="LlmCompletionResult"/> for tests
    /// to enqueue. The exact JSON shape mirrors
    /// <see cref="PortfolioSkillFindingDto"/>'s serialization (PascalCase
    /// property names via default <see cref="System.Text.Json"/> options).</summary>
    private static LlmCompletionResult SkillsJson(params (string Skill, string Band, string Explanation)[] skills)
    {
        var dtoArray = skills.Select(s => new PortfolioSkillFindingDto(s.Skill, s.Band, s.Explanation)).ToArray();
        var json = System.Text.Json.JsonSerializer.Serialize(dtoArray);
        return new LlmCompletionResult(json, ModelUsed: "test-model");
    }

    /// <summary>Builds a processor wired to the given context + LLM fake. The
    /// extractor is a real instance pointed at the same <see cref="FakeArtifactStore"/>
    /// we use elsewhere — its evidence paths are well-understood so the test
    /// can exercise either branch (link / file / unsupported). The ambient
    /// account context is the real <see cref="AmbientAccountContext"/> so the
    /// processor's new scope bracket (claim under system, work under account)
    /// is exercised end-to-end; a hand-built fake would silently swallow the
    /// same bug STOR-40 reproduces on production Postgres.</summary>
    private static (PortfolioAnalysisJobProcessor Processor, AmbientAccountContext Account) CreateProcessor(
        WriteDbContext dbContext,
        ILlmClient llmClient)
    {
        return CreateProcessorWithRealScope(dbContext, llmClient);
    }

    /// <summary>Seeds one <c>Pending</c> analysis job linked to a freshly-created
    /// <see cref="PortfolioItem"/>, then returns a fresh
    /// <see cref="WriteDbContext"/> the test can pass into the processor.</summary>
    private static async Task<(WriteDbContext DbContext, Guid AccountId, Guid PortfolioItemId)> SeedPendingJobAsync(
        string submissionType,
        string category,
        string? storageKey = null,
        string? contentType = null,
        string label = "Item",
        string? description = null)
    {
        var accountId = Guid.NewGuid();
        var portfolioItemId = Guid.NewGuid();
        var databaseName = SharedPendingJobDatabase;
        var sharedRoot = SharedPendingJobRoot;

        await using (var seedContext = CreateDbContextOnSharedRoot(databaseName, sharedRoot, accountId))
        {
            seedContext.PortfolioItems.Add(new PortfolioItem
            {
                Id = portfolioItemId,
                AccountId = accountId,
                Label = label,
                Category = category,
                SubmissionType = submissionType,
                ExternalUrl = submissionType == PortfolioSubmissionTypes.Link ? "https://example.com/x" : null,
                StorageKey = storageKey,
                ContentType = contentType,
                Description = description,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            seedContext.Jobs.Add(new Job
            {
                Id = Guid.NewGuid(),
                Type = PortfolioJobTypes.AnalyzePortfolioItem,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new AnalyzePortfolioItemPayload(portfolioItemId)),
                Status = JobStatus.Pending,
                AttemptCount = 0,
                AccountId = accountId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            await seedContext.SaveChangesAsync();
        }

        return (CreateDbContextOnSharedRoot(databaseName, sharedRoot, accountId), accountId, portfolioItemId);
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

    /// <summary>
    /// Build a processor with the REAL <see cref="AmbientAccountContext"/> /
    /// <see cref="BackgroundAccountScope"/> pair (rather than a hand-built
    /// scoped reader). The processor is now expected to bracket its claim
    /// with the scope itself, so any test that bypasses the scope will fail
    /// the same way the production bug did — exactly the reason STOR-40
    /// replaces the hand-built context the prior suite used.
    /// </summary>
    private static (PortfolioAnalysisJobProcessor Processor, AmbientAccountContext AccountContext) CreateProcessorWithRealScope(
        WriteDbContext dbContext,
        ILlmClient llmClient)
    {
        var artifactStore = new FakeArtifactStore();
        var extractor = new EvidenceContentExtractor(artifactStore, NullLogger<EvidenceContentExtractor>.Instance);
        var ambient = new AmbientAccountContext();
        var scope = new BackgroundAccountScope(ambient, ambient);
        var processor = new PortfolioAnalysisJobProcessor(
            dbContext,
            llmClient,
            extractor,
            NullLogger<PortfolioAnalysisJobProcessor>.Instance,
            TimeProvider.System,
            scope);
        return (processor, ambient);
    }

    // Shared store name + root used by every seed / read in this suite. The
    // shared root is what lets seed and processor see each other's rows even
    // though they live on different DbContext instances (mirrors the pattern
    // RowLevelSecurityInterceptorTests uses for its own cross-context reads).
    private const string SharedPendingJobDatabase = "PortfolioAnalysisProcessorTests";
    private static readonly InMemoryDatabaseRoot SharedPendingJobRoot = new();

    /// <summary>Plain property-bag test double for <see cref="IAccountContext"/>
    /// matching the shape <see cref="Storporate.Tests.Unit.Persistence.PortfolioSkillFindingTenantIsolationTests"/>
    /// uses for its query-filter coverage.</summary>
    private sealed class TestAccountContext : IAccountContext
    {
        public Guid? UserId { get; init; }
        public Guid? AccountId { get; init; }
        public bool IsAdministrator { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
    }
}
