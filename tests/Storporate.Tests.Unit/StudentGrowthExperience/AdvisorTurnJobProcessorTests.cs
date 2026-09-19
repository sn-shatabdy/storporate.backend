using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.Modules.StudentGrowthExperience;
using Storporate.Modules.StudentGrowthExperience.Advisor;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.StudentGrowthExperience;

/// <summary>
/// Direct-processor coverage for <see cref="AdvisorTurnJobProcessor"/>.
/// Sits one layer below the integration tests: it still goes through the
/// real EF Core in-memory provider, the real
/// <see cref="JobClaimer"/> / <see cref="JobBookkeeper"/>, the real
/// <see cref="AdvisorPromptBuilder"/> / <see cref="AdvisorResponseParser"/>,
/// and the real two-scope account bracket — but resolves the processor by
/// hand rather than going through HTTP, so the assertions can target the
/// processor's own state transitions directly.
///
/// Pinned behaviors:
/// <list type="bullet">
///   <item>Opening happy path: message written, summary v1 written, status Idle, job Succeeded.</item>
///   <item>Opening failure-then-succeeded on retry: 2nd tick succeeds, attempt 1's failure is reflected in the bookkeeper.</item>
///   <item>Opening failure after 3 attempts: job Failed, exploration Failed, LastError populated with friendly text.</item>
///   <item>OnJobAbandonedAsync flips a Working exploration to Failed with the friendly error.</item>
///   <item>Duplicate context note is not stored twice.</item>
/// </list>
/// </summary>
public class AdvisorTurnJobProcessorTests
{
    [Fact]
    public async Task OpeningTurn_HappyPath_WritesMessageSummaryV1AndFlipsJobToSucceeded()
    {
        var fixture = await SeedOpeningJobAsync();

        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: """
            {
              "reply": "Welcome!",
              "questions": [{"prompt":"What area?","options":["A","B"]}],
              "contextNotes": ["first note"],
              "summary": {"gaps": [{"title":"Practice","detail":"none yet","band":"Developing"}], "suggestions": []}
            }
            """,
            ModelUsed: "test-model"));

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);

        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);

        var messages = await fixture.Db.ExplorationMessages.AsNoTracking().ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Role == ExplorationRoles.Student && m.Content == "seed direction");
        Assert.Contains(messages, m => m.Role == ExplorationRoles.Advisor && m.Content == "Welcome!");

        var notes = await fixture.Db.StudentContextNotes.AsNoTracking().ToListAsync();
        Assert.Single(notes);
        Assert.Equal("first note", notes[0].Text);

        var summary = await fixture.Db.ExplorationSummaryVersions.AsNoTracking().SingleAsync();
        Assert.Equal(1, summary.VersionNumber);
        Assert.Null(summary.ChangeNote);

        var exploration = await fixture.Db.Explorations.AsNoTracking()
            .SingleAsync(e => e.Id == fixture.ExplorationId);
        Assert.Equal(ExplorationStatuses.Idle, exploration.Status);
        Assert.Null(exploration.LastError);
    }

    [Fact]
    public async Task OpeningTurn_WithLlmFailureThenSuccess_RequeuesAndThenSucceeds()
    {
        var fixture = await SeedOpeningJobAsync();

        fixture.LlmClient.EnqueueException(new LlmProviderException("first attempt failed"));
        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: """{"reply":"ok after retry"}""",
            ModelUsed: "test-model"));

        var firstOutcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, firstOutcome);

        var jobAfterFirst = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Pending, jobAfterFirst.Status);
        Assert.Equal(1, jobAfterFirst.AttemptCount);

        var secondOutcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, secondOutcome);

        var jobAfterSecond = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, jobAfterSecond.Status);
    }

    [Fact]
    public async Task OpeningTurn_AfterThreeLlmFailures_JobAndExplorationAreBothFailed()
    {
        var fixture = await SeedOpeningJobAsync();

        fixture.LlmClient.EnqueueException(new LlmProviderException("fail 1"));
        fixture.LlmClient.EnqueueException(new LlmProviderException("fail 2"));
        fixture.LlmClient.EnqueueException(new LlmProviderException("fail 3"));

        for (var i = 0; i < JobBookkeeper.MaxAttempts; i++)
        {
            var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
            Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        }

        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobBookkeeper.MaxAttempts, job.AttemptCount);
        Assert.Contains("fail 3", job.ErrorMessage);

        var exploration = await fixture.Db.Explorations.AsNoTracking()
            .SingleAsync(e => e.Id == fixture.ExplorationId);
        Assert.Equal(ExplorationStatuses.Failed, exploration.Status);
        Assert.NotNull(exploration.LastError);
        Assert.Contains("Try again", exploration.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnJobAbandonedAsync_WorkingExploration_FlipsToFailedWithFriendlyError()
    {
        var fixture = await SeedOpeningJobAsync();
        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();

        await fixture.Processor.OnJobAbandonedAsync(job, CancellationToken.None);

        var exploration = await fixture.Db.Explorations.AsNoTracking()
            .SingleAsync(e => e.Id == fixture.ExplorationId);
        Assert.Equal(ExplorationStatuses.Failed, exploration.Status);
        Assert.NotNull(exploration.LastError);
        Assert.Equal(AdvisorDefaults.FriendlyAdvisorError, exploration.LastError);
    }

    [Fact]
    public async Task OpeningTurn_DuplicateContextNote_NotStoredTwice()
    {
        var fixture = await SeedOpeningJobAsync();

        // Seed an existing note with the exact text the LLM will return.
        fixture.Db.StudentContextNotes.Add(new StudentContextNote
        {
            Id = Guid.NewGuid(),
            AccountId = fixture.AccountId,
            ExplorationId = Guid.NewGuid(),
            Text = "already-known note",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        fixture.LlmClient.EnqueueResponse(new LlmCompletionResult(
            OutputText: """
            {
              "reply": "ok",
              "contextNotes": ["already-known note", "brand-new note"]
            }
            """,
            ModelUsed: "test-model"));

        await fixture.Processor.TryProcessOneAsync(CancellationToken.None);

        var notes = await fixture.Db.StudentContextNotes
            .AsNoTracking()
            .Where(n => n.AccountId == fixture.AccountId)
            .ToListAsync();

        // Existing row preserved; only the new one was written.
        Assert.Equal(2, notes.Count);
        Assert.Single(notes, n => n.Text == "already-known note");
        Assert.Single(notes, n => n.Text == "brand-new note");
    }

    // ----- infrastructure -----

    private async Task<Fixture> SeedOpeningJobAsync()
    {
        var accountId = Guid.NewGuid();
        var explorationId = Guid.NewGuid();
        var databaseName = "AdvisorTurnJobProcessorTests-" + Guid.NewGuid().ToString("N");
        var sharedRoot = new InMemoryDatabaseRoot();
        var llmClient = new FakeLlmClient();

        await using (var seedContext = CreateDbContextOnSharedRoot(
                         databaseName, sharedRoot, accountId, llmClient))
        {
            seedContext.Explorations.Add(new Exploration
            {
                Id = explorationId,
                AccountId = accountId,
                Title = AdvisorDefaults.DefaultExplorationTitle,
                Status = ExplorationStatuses.Working,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            seedContext.ExplorationMessages.Add(new ExplorationMessage
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ExplorationId = explorationId,
                Role = ExplorationRoles.Student,
                Content = "seed direction",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            seedContext.Jobs.Add(new Job
            {
                Id = Guid.NewGuid(),
                Type = GrowthJobTypes.AdvisorTurn,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                    new AdvisorTurnPayload(explorationId, AdvisorTurnModes.Opening)),
                Status = JobStatus.Pending,
                AttemptCount = 0,
                AccountId = accountId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        var dbContext = CreateDbContextOnSharedRoot(
            databaseName, sharedRoot, accountId, llmClient);
        var ambient = new AmbientAccountContext();
        var scope = new BackgroundAccountScope(ambient, ambient);
        var processor = new AdvisorTurnJobProcessor(
            dbContext,
            llmClient,
            Options.Create(new AdvisorOptions()),
            NullLogger<AdvisorTurnJobProcessor>.Instance,
            TimeProvider.System,
            scope);
        return new Fixture(dbContext, processor, llmClient, accountId, explorationId);
    }

    private static WriteDbContext CreateDbContextOnSharedRoot(
        string databaseName,
        InMemoryDatabaseRoot sharedRoot,
        Guid accountId,
        // LlmClient is intentionally unused inside the factory; the
        // processor receives it through its own constructor, so the
        // argument exists only to keep the factory signature stable and
        // the test code symmetrical.
        // ReSharper disable once UnusedParameter
        ILlmClient llmClient)
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
        AdvisorTurnJobProcessor Processor,
        FakeLlmClient LlmClient,
        Guid AccountId,
        Guid ExplorationId);
}
