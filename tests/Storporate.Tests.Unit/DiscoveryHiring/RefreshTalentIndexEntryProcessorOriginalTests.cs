using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.Infrastructure.Persistence.TalentIndex;
using Storporate.Modules.DiscoveryHiring.TalentIndex;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-44 Phase 1: per-item drill-down descriptor projection by
/// <see cref="RefreshTalentIndexEntryProcessor"/>. Sits alongside
/// <see cref="RefreshTalentIndexEntryProcessorTests"/>; same plumbing, but
/// every test exercises one specific shape of the new <c>original</c>
/// property on the item snapshot and one specific input to the
/// <c>ContentHash</c> over the per-item sharing flag.
/// </summary>
/// <remarks>
/// <para>
/// All four tests use the in-memory EF provider so the assertions target the
/// processor's own state transitions directly — exactly the shape that goes
/// into <see cref="TalentIndexEntry.ItemsJson"/> and
/// <see cref="TalentIndexEntry.ContentHash"/> on disk.
/// </para>
/// <para>
/// Pinned behaviors:
/// <list type="bullet">
///   <item>File submission, flag ON: <c>original.kind=File</c>, all five file
///   fields populated, URL is null.</item>
///   <item>Link submission, flag ON: <c>original.kind=Link</c>, URL populated,
///   file fields are null.</item>
///   <item>Flag OFF for either submission: <c>original</c> property is absent
///   from the JSON (the <c>JsonIgnore(WhenWritingNull)</c> contract).</item>
///   <item>Flag OFF: <c>SearchText</c> excludes the file name, the URL, and the
///   storage key (sensitive items that RLS hides from the Organization anyway).</item>
///   <item>Flag toggle: changing <c>ShareOriginalWithEmployers</c> on an item
///   changes <c>ContentHash</c> so the second tick re-embeds.</item>
/// </list>
/// </para>
/// </remarks>
public class RefreshTalentIndexEntryProcessorOriginalTests
{
    [Fact]
    public async Task FileSubmission_FlagOn_PopulatesOriginalKindFileDescriptor()
    {
        var fixture = await SeedPendingRefreshJobAsync(
            submissionType: PortfolioSubmissionTypes.File,
            shareOriginal: true,
            storageKey: "artifacts/abc/seed.pdf",
            originalFileName: "capstone-report.pdf",
            contentType: "application/pdf",
            fileSizeBytes: 1_234_567L,
            externalUrl: null);

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        var entry = await fixture.Db.TalentIndexEntries.AsNoTracking()
            .SingleAsync(e => e.StudentAccountId == fixture.AccountId);
        var snapshot = DeserializeSingleSnapshot(entry.ItemsJson);

        Assert.NotNull(snapshot.Original);
        Assert.Equal("File", snapshot.Original!.Kind);
        Assert.Equal("capstone-report.pdf", snapshot.Original.FileName);
        Assert.Equal("application/pdf", snapshot.Original.ContentType);
        Assert.Equal(1_234_567L, snapshot.Original.SizeBytes);
        Assert.Equal("artifacts/abc/seed.pdf", snapshot.Original.StorageKey);
        Assert.Null(snapshot.Original.Url);
    }

    [Fact]
    public async Task LinkSubmission_FlagOn_PopulatesOriginalKindLinkDescriptor()
    {
        var fixture = await SeedPendingRefreshJobAsync(
            submissionType: PortfolioSubmissionTypes.Link,
            shareOriginal: true,
            storageKey: null,
            originalFileName: null,
            contentType: null,
            fileSizeBytes: null,
            externalUrl: "https://github.com/example/repo");

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        var entry = await fixture.Db.TalentIndexEntries.AsNoTracking()
            .SingleAsync(e => e.StudentAccountId == fixture.AccountId);
        var snapshot = DeserializeSingleSnapshot(entry.ItemsJson);

        Assert.NotNull(snapshot.Original);
        Assert.Equal("Link", snapshot.Original!.Kind);
        Assert.Equal("https://github.com/example/repo", snapshot.Original.Url);
        Assert.Null(snapshot.Original.FileName);
        Assert.Null(snapshot.Original.ContentType);
        Assert.Null(snapshot.Original.SizeBytes);
        Assert.Null(snapshot.Original.StorageKey);
    }

    [Fact]
    public async Task FlagOff_OriginalPropertyIsAbsentFromItemsJson()
    {
        var fixture = await SeedPendingRefreshJobAsync(
            submissionType: PortfolioSubmissionTypes.File,
            shareOriginal: false,
            storageKey: "artifacts/abc/seed.pdf",
            originalFileName: "capstone-report.pdf",
            contentType: "application/pdf",
            fileSizeBytes: 1_234_567L,
            externalUrl: null);

        var outcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);

        Assert.Equal(BackgroundJobTickOutcome.Processed, outcome);
        var entry = await fixture.Db.TalentIndexEntries.AsNoTracking()
            .SingleAsync(e => e.StudentAccountId == fixture.AccountId);

        // The flag-off case must carry no `original` property at all — the
        // JsonIgnore(WhenWritingNull) contract. Confirm via raw JSON so a
        // regression that flips the attribute is caught.
        using var doc = JsonDocument.Parse(entry.ItemsJson);
        var firstRow = doc.RootElement[0];
        Assert.False(
            firstRow.TryGetProperty("original", out _),
            "Flag-off case must omit the `original` property from the snapshot.");

        // Round-trip via the record too, to keep the assertion honest about
        // the typed shape.
        var snapshot = DeserializeSingleSnapshot(entry.ItemsJson);
        Assert.Null(snapshot.Original);
    }

    [Fact]
    public async Task FlagOff_SearchTextExcludesFileNameAndStorageKey()
    {
        // The sensitive-by-RLS items — file name, storage key, external URL
        // — must never reach the embedding input. Even though the
        // ContentHash legitimately hashes them (so a metadata change
        // triggers re-embedding), SearchText stays restricted to label /
        // category / skill / band. Phase 2 reads SearchText as the only
        // embedding source.
        var fixture = await SeedPendingRefreshJobAsync(
            submissionType: PortfolioSubmissionTypes.File,
            shareOriginal: false,
            storageKey: "artifacts/abc/secret-storage-key",
            originalFileName: "secret-filename.pdf",
            contentType: "application/pdf",
            fileSizeBytes: 9_999L,
            externalUrl: null);

        await fixture.Processor.TryProcessOneAsync(CancellationToken.None);

        var entry = await fixture.Db.TalentIndexEntries.AsNoTracking()
            .SingleAsync(e => e.StudentAccountId == fixture.AccountId);

        Assert.DoesNotContain("secret-filename.pdf", entry.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-storage-key", entry.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain("application/pdf", entry.SearchText, StringComparison.Ordinal);
        // The label and skill names should be present (sanity check the
        // builder didn't accidentally drop everything).
        Assert.Contains("Seed Item", entry.SearchText, StringComparison.Ordinal);
        Assert.Contains("React", entry.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlagToggle_ChangesContentHash_SoSecondTickReEmbeds()
    {
        // First tick: flag OFF → entry populated, hash reflects share=off.
        // Flip the flag to ON, re-enqueue, second tick must see a different
        // hash and therefore a second embedding call.
        var fixture = await SeedPendingRefreshJobAsync(
            submissionType: PortfolioSubmissionTypes.File,
            shareOriginal: false,
            storageKey: "artifacts/abc/seed.pdf",
            originalFileName: "capstone-report.pdf",
            contentType: "application/pdf",
            fileSizeBytes: 1_234_567L,
            externalUrl: null);

        var firstOutcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, firstOutcome);
        Assert.Equal(1, fixture.EmbeddingClient.CallCount);

        var firstEntry = await fixture.Db.TalentIndexEntries.AsNoTracking()
            .SingleAsync(e => e.StudentAccountId == fixture.AccountId);
        var firstHash = firstEntry.ContentHash;
        Assert.False(string.IsNullOrEmpty(firstHash));

        // Flip the flag and re-enqueue.
        var item = await fixture.Db.PortfolioItems
            .SingleAsync(i => i.AccountId == fixture.AccountId);
        item.ShareOriginalWithEmployers = true;
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

        var secondOutcome = await fixture.Processor.TryProcessOneAsync(CancellationToken.None);
        Assert.Equal(BackgroundJobTickOutcome.Processed, secondOutcome);
        // The hash changed → the embedding call ran again.
        Assert.Equal(2, fixture.EmbeddingClient.CallCount);

        var secondEntry = await fixture.Db.TalentIndexEntries.AsNoTracking()
            .SingleAsync(e => e.StudentAccountId == fixture.AccountId);
        Assert.NotEqual(firstHash, secondEntry.ContentHash);

        // And the stored snapshot now carries the file descriptor.
        var snapshot = DeserializeSingleSnapshot(secondEntry.ItemsJson);
        Assert.NotNull(snapshot.Original);
        Assert.Equal("File", snapshot.Original!.Kind);
        Assert.Equal("capstone-report.pdf", snapshot.Original.FileName);
    }

    // ----- infrastructure -----

    private static TalentIndexItemSnapshot DeserializeSingleSnapshot(string itemsJson)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
        var snapshots = JsonSerializer.Deserialize<List<TalentIndexItemSnapshot>>(itemsJson, options)
            ?? new List<TalentIndexItemSnapshot>();
        return Assert.Single(snapshots);
    }

    private async Task<Fixture> SeedPendingRefreshJobAsync(
        string submissionType,
        bool shareOriginal,
        string? storageKey,
        string? originalFileName,
        string? contentType,
        long? fileSizeBytes,
        string? externalUrl)
    {
        var accountId = Guid.NewGuid();
        var databaseName = "RefreshTalentIndexEntryProcessorOriginalTests-"
            + Guid.NewGuid().ToString("N");
        var sharedRoot = new InMemoryDatabaseRoot();
        var embeddingClient = new FakeEmbeddingClient();

        await using (var seedContext = CreateDbContextOnSharedRoot(
                         databaseName, sharedRoot, accountId))
        {
            seedContext.StudentSearchProfiles.Add(new StudentSearchProfile
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                IsSearchable = true,
                DisplayName = "Seed Student",
                OptedInAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            var itemId = Guid.NewGuid();
            seedContext.PortfolioItems.Add(new PortfolioItem
            {
                Id = itemId,
                AccountId = accountId,
                Label = "Seed Item",
                Category = PortfolioCategories.Document,
                SubmissionType = submissionType,
                StorageKey = storageKey,
                OriginalFileName = originalFileName,
                ContentType = contentType,
                FileSizeBytes = fileSizeBytes,
                ExternalUrl = externalUrl,
                ShareOriginalWithEmployers = shareOriginal,
                CreatedAt = DateTimeOffset.UtcNow,
                AnalysisStatus = PortfolioAnalysisStatuses.Analyzed,
                LastAnalyzedAt = DateTimeOffset.UtcNow,
            });

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

        return new Fixture(dbContext, processor, embeddingClient, accountId);
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
        Guid AccountId);
}
