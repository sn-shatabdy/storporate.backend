using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.Infrastructure.Persistence.TalentIndex;
using Storporate.Modules.DiscoveryHiring.TalentIndex;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-43 Phase 1: direct-processor coverage for
/// <see cref="RefreshTalentIndexEntryProcessor"/>. Sits one layer below the
/// endpoint integration tests: it goes through the real EF Core in-memory
/// provider, the real <see cref="JobClaimer"/> / <see cref="JobBookkeeper"/>,
/// the real <see cref="TalentIndexRepository"/> (InMemory branch), and the
/// real <see cref="BackgroundAccountScope"/>, but resolves the processor by
/// hand rather than going through HTTP, so the assertions can target the
/// processor's own state transitions directly.
/// </summary>
/// <remarks>
/// Pinned behaviors:
/// <list type="bullet">
///   <item>Happy path: profile opted in, one analyzed item with one Strong finding → entry upserted with embedding, job Succeeded.</item>
///   <item>Opt-out between enqueue and claim: profile is non-searchable → entry deleted (no-op when absent), job Succeeded.</item>
///   <item>No qualifying items: opted in but no Strong/Developing findings → entry deleted (no-op when absent), job Succeeded.</item>
///   <item>Embedding provider failure: 3 retries → job Failed (after the 3rd attempt), entry stays at prior state.</item>
///   <item>Content-hash skip: re-running on identical projection inputs skips the embedding call entirely.</item>
///   <item>No pending job: tick returns <see cref="BackgroundJobTickOutcome.NoWork"/>.</item>
/// </list>
/// </remarks>
public class RefreshTalentIndexEntryProcessorTests
{
    [Fact]
    public async Task HappyPath_OptedInWithOneAnalyzedItem_UpsertsEntryAndMarksJobSucceeded()
    {
        var fixture = await SeedPendingRefreshJobAsync(
            isSearchable: true,
            seedAnalyzedItem: true,
            includeStrongFinding: true);

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(1, fixture.EmbeddingClient.CallCount);

        var entry = await fixture.Db.TalentIndexEntries.AsNoTracking()
            .SingleAsync(e => e.StudentAccountId == fixture.AccountId);
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal("Seed Student", entry.DisplayName);
        Assert.False(string.IsNullOrEmpty(entry.ContentHash));
        Assert.False(string.IsNullOrEmpty(entry.ItemsJson));
        Assert.False(string.IsNullOrEmpty(entry.SearchText));
    }

    [Fact]
    public async Task OptedOutAtClaimTime_DeletesEntryAndMarksJobSucceeded()
    {
        // STOR-43: between enqueue (a student was opted in) and claim (the
        // student opted out), the profile's IsSearchable is now false. The
        // handler that flipped the toggle to false already deleted the
        // entry; the processor must simply leave it deleted and complete the
        // job without an LLM call.
        var fixture = await SeedPendingRefreshJobAsync(
            isSearchable: false,
            seedAnalyzedItem: false,
            includeStrongFinding: false);

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        Assert.Equal(0, fixture.EmbeddingClient.CallCount);
        Assert.Empty(await fixture.Db.TalentIndexEntries.AsNoTracking().ToListAsync());
        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);
    }

    [Fact]
    public async Task OptedInButNoQualifyingItems_DeletesEntryAndMarksJobSucceeded()
    {
        // The student is opted in but has zero analyzed items with
        // Strong/Developing findings. The processor must take the
        // "no qualifying items" branch: delete any prior entry, complete
        // the job, no LLM call.
        var fixture = await SeedPendingRefreshJobAsync(
            isSearchable: true,
            seedAnalyzedItem: false,
            includeStrongFinding: false);

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        Assert.Equal(0, fixture.EmbeddingClient.CallCount);
        Assert.Empty(await fixture.Db.TalentIndexEntries.AsNoTracking().ToListAsync());
        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);
    }

    [Fact]
    public async Task OptedInWithOnlyMissingFindings_ItemIsExcludedAndEntryIsDeleted()
    {
        // An analyzed item with only Missing-band findings has nothing to
        // contribute to the projection — it should be filtered out at the
        // SQL level. With no qualifying items left the processor takes the
        // "delete entry, complete job" path.
        var fixture = await SeedPendingRefreshJobAsync(
            isSearchable: true,
            seedAnalyzedItem: true,
            includeStrongFinding: false,
            includeMissingFinding: true);

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        Assert.Equal(0, fixture.EmbeddingClient.CallCount);
        Assert.Empty(await fixture.Db.TalentIndexEntries.AsNoTracking().ToListAsync());
        var job = await fixture.Db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Succeeded, job.Status);
    }

    [Fact]
    public async Task EmbeddingFailureAfterThreeAttempts_JobIsFailed()
    {
        var fixture = await SeedPendingRefreshJobAsync(
            isSearchable: true,
            seedAnalyzedItem: true,
            includeStrongFinding: true);

        // 3 LLM failures → job Failed on the 3rd attempt.
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
        // The Job.ErrorMessage column is sanitized — the processor no longer
        // includes the raw provider exception text so a hosted provider
        // that echoes its input prompt in the 4xx body can't leak it into
        // the audit chain. The fixed reason is what the operator sees.
        Assert.Contains("Embedding provider error", job.ErrorMessage);
    }

    [Fact]
    public async Task ContentHashUnchanged_OnSecondTick_EmbeddingIsNotCalled()
    {
        // The first tick populates the entry. The second tick recomputes
        // the projection's content hash, finds it identical, and short-
        // circuits the embedding call — saving an LLM call on a no-op
        // refresh. The job still moves to Succeeded.
        var fixture = await SeedPendingRefreshJobAsync(
            isSearchable: true,
            seedAnalyzedItem: true,
            includeStrongFinding: true);

        // Seed a second refresh job — the first one we tick, the second
        // one we tick and assert no embedding call.
        await fixture.Db.Jobs.AddAsync(new Job
        {
            Id = Guid.NewGuid(),
            Type = TalentIndexJobTypes.RefreshEntry,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new RefreshTalentIndexPayload(fixture.AccountId)),
            Status = JobStatus.Pending,
            AttemptCount = 0,
            AccountId = fixture.AccountId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var firstOutcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, firstOutcome);
        Assert.Equal(1, fixture.EmbeddingClient.CallCount);

        var secondOutcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, secondOutcome);
        // The second tick saw an unchanged projection — it must NOT call
        // the embedding client again.
        Assert.Equal(1, fixture.EmbeddingClient.CallCount);

        var jobs = await fixture.Db.Jobs.AsNoTracking().OrderBy(j => j.CreatedAt).ToListAsync();
        Assert.All(jobs, j => Assert.Equal(JobStatus.Succeeded, j.Status));
    }

    [Fact]
    public async Task NoPendingJob_TickReturnsNoWork()
    {
        var fixture = await SeedPendingRefreshJobAsync(
            isSearchable: true,
            seedAnalyzedItem: true,
            includeStrongFinding: true);

        // Clear the seeded job so the processor has nothing to claim.
        var jobs = await fixture.Db.Jobs.ToListAsync();
        fixture.Db.Jobs.RemoveRange(jobs);
        await fixture.Db.SaveChangesAsync();

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.NoWork, outcome);
        Assert.Equal(0, fixture.EmbeddingClient.CallCount);
    }

    [Fact]
    public async Task ItemDeletedBetweenRefreshes_NewItemsJsonOmitsRemovedItemId()
    {
        // First tick: student has two analyzed items with Strong findings,
        // so the entry's ItemsJson contains both item ids.
        var fixture = await SeedPendingRefreshJobAsync(
            isSearchable: true,
            seedAnalyzedItem: true,
            includeStrongFinding: true,
            extraAnalyzedItem: true);

        var firstOutcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, firstOutcome);

        var firstEntry = await fixture.Db.TalentIndexEntries.AsNoTracking()
            .SingleAsync(e => e.StudentAccountId == fixture.AccountId);
        var firstIds = ExtractPortfolioItemIds(firstEntry.ItemsJson);
        Assert.Equal(2, firstIds.Count);

        // The student deletes the first item via the portfolio DELETE
        // endpoint (here simulated by removing the EF row directly — the
        // production handler does the same EF remove). Then enqueue a
        // second refresh job so the processor reprojects the index.
        var survivingItemId = firstIds.First(id =>
            id != fixture.FirstSeededItemId);
        var deletedItemId = fixture.FirstSeededItemId;
        fixture.Db.PortfolioItems.RemoveRange(
            fixture.Db.PortfolioItems
                .Where(i => i.AccountId == fixture.AccountId && i.Id == deletedItemId));
        fixture.Db.PortfolioSkillFindings.RemoveRange(
            fixture.Db.PortfolioSkillFindings
                .Where(f => f.AccountId == fixture.AccountId && f.PortfolioItemId == deletedItemId));
        fixture.Db.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = TalentIndexJobTypes.RefreshEntry,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new RefreshTalentIndexPayload(fixture.AccountId)),
            Status = JobStatus.Pending,
            AttemptCount = 0,
            AccountId = fixture.AccountId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        // Second tick reprojects. The ItemsJson must contain only the
        // surviving item id, not the deleted one. Content-hash differs so
        // the embedding call runs again (1 from the first tick + 1 here).
        var secondOutcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, secondOutcome);
        Assert.Equal(2, fixture.EmbeddingClient.CallCount);

        var secondEntry = await fixture.Db.TalentIndexEntries.AsNoTracking()
            .SingleAsync(e => e.StudentAccountId == fixture.AccountId);
        var secondIds = ExtractPortfolioItemIds(secondEntry.ItemsJson);
        Assert.Single(secondIds);
        Assert.Contains(survivingItemId, secondIds);
        Assert.DoesNotContain(deletedItemId, secondIds);
    }

    /// <summary>Parse the stored JSON to recover the ordered list of
    /// <c>portfolioItemId</c> values for assertion. The processor writes
    /// <see cref="TalentIndexItemSnapshot"/> rows; their <c>portfolioItemId</c>
    /// field is the assertion target.</summary>
    private static List<Guid> ExtractPortfolioItemIds(string itemsJson)
    {
        var options = new System.Text.Json.JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
        var snapshots = System.Text.Json.JsonSerializer
            .Deserialize<List<TalentIndexItemSnapshot>>(itemsJson, options)
            ?? new List<TalentIndexItemSnapshot>();
        return snapshots.Select(s => s.PortfolioItemId).ToList();
    }

    // ----- infrastructure -----

    private async Task<Fixture> SeedPendingRefreshJobAsync(
        bool isSearchable,
        bool seedAnalyzedItem,
        bool includeStrongFinding,
        bool includeMissingFinding = false,
        bool extraAnalyzedItem = false)
    {
        var accountId = Guid.NewGuid();
        var databaseName = "RefreshTalentIndexEntryProcessorTests-" + Guid.NewGuid().ToString("N");
        var sharedRoot = new InMemoryDatabaseRoot();
        var embeddingClient = new FakeEmbeddingClient();
        var firstSeededItemId = Guid.Empty;

        await using (var seedContext = CreateDbContextOnSharedRoot(
                         databaseName, sharedRoot, accountId))
        {
            seedContext.StudentSearchProfiles.Add(new StudentSearchProfile
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                IsSearchable = isSearchable,
                DisplayName = isSearchable ? "Seed Student" : string.Empty,
                OptedInAt = isSearchable ? DateTimeOffset.UtcNow : null,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            if (seedAnalyzedItem)
            {
                var itemId = Guid.NewGuid();
                firstSeededItemId = itemId;
                seedContext.PortfolioItems.Add(new PortfolioItem
                {
                    Id = itemId,
                    AccountId = accountId,
                    Label = "Seed Item",
                    Category = PortfolioCategories.Document,
                    SubmissionType = PortfolioSubmissionTypes.Link,
                    ExternalUrl = "https://example.com/seed",
                    CreatedAt = DateTimeOffset.UtcNow,
                    AnalysisStatus = PortfolioAnalysisStatuses.Analyzed,
                    LastAnalyzedAt = DateTimeOffset.UtcNow,
                });

                if (includeStrongFinding)
                {
                    seedContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
                    {
                        Id = Guid.NewGuid(),
                        AccountId = accountId,
                        PortfolioItemId = itemId,
                        SkillName = "React",
                        ConfidenceBand = ConfidenceBands.Strong,
                        Explanation = "Built a component-based UI in the seed item.",
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }

                if (includeMissingFinding)
                {
                    seedContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
                    {
                        Id = Guid.NewGuid(),
                        AccountId = accountId,
                        PortfolioItemId = itemId,
                        SkillName = "Rust",
                        ConfidenceBand = ConfidenceBands.Missing,
                        Explanation = "No Rust code in the seed item.",
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }

                if (extraAnalyzedItem)
                {
                    var secondItemId = Guid.NewGuid();
                    seedContext.PortfolioItems.Add(new PortfolioItem
                    {
                        Id = secondItemId,
                        AccountId = accountId,
                        Label = "Seed Item Two",
                        Category = PortfolioCategories.Document,
                        SubmissionType = PortfolioSubmissionTypes.Link,
                        ExternalUrl = "https://example.com/seed-two",
                        CreatedAt = DateTimeOffset.UtcNow,
                        AnalysisStatus = PortfolioAnalysisStatuses.Analyzed,
                        LastAnalyzedAt = DateTimeOffset.UtcNow,
                    });
                    seedContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
                    {
                        Id = Guid.NewGuid(),
                        AccountId = accountId,
                        PortfolioItemId = secondItemId,
                        SkillName = "SQL",
                        ConfidenceBand = ConfidenceBands.Strong,
                        Explanation = "Schema design work in the second item.",
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }
            }

            seedContext.Jobs.Add(new Job
            {
                Id = Guid.NewGuid(),
                Type = TalentIndexJobTypes.RefreshEntry,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                    new RefreshTalentIndexPayload(accountId)),
                Status = JobStatus.Pending,
                AttemptCount = 0,
                AccountId = accountId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            await seedContext.SaveChangesAsync();
        }

        var dbContext = CreateDbContextOnSharedRoot(databaseName, sharedRoot, accountId);
        var ambient = new AmbientAccountContext();
        var scope = new BackgroundAccountScope(ambient, ambient);
        var repository = new TalentIndexRepository(
            dbContext,
            Options.Create(new ConnectionStringsOptions
            {
                WriteDb = "Host=localhost;Database=test;Username=u;Password=p",
                SslMode = "Disable",
            }),
            NullLogger<TalentIndexRepository>.Instance);
        var processor = new RefreshTalentIndexEntryProcessor(
            dbContext,
            repository,
            embeddingClient,
            NullLogger<RefreshTalentIndexEntryProcessor>.Instance,
            TimeProvider.System,
            scope);

        return new Fixture(dbContext, processor, embeddingClient, accountId, firstSeededItemId);
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
        RefreshTalentIndexEntryProcessor Processor,
        FakeEmbeddingClient EmbeddingClient,
        Guid AccountId,
        Guid FirstSeededItemId);
}