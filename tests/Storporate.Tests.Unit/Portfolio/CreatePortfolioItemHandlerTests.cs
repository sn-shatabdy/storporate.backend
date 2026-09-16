using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.Modules.Portfolio.Analysis;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Storage;
using Storporate.Tests.Unit.Portfolio;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// TDD coverage for <see cref="CreatePortfolioItemHandler"/>: file submissions stream
/// through <see cref="IArtifactStore"/> under a server-generated key and produce a
/// DB row with the file metadata populated; link submissions skip storage entirely
/// and produce only the DB row with the URL. The artifact key is never derived
/// from the client-supplied filename (path-traversal mitigation).
/// </summary>
/// <remarks>
/// <para>
/// <b>STOR-38 Phase 2.</b> Every accepted submission also enqueues one
/// <see cref="Job"/> of type <see cref="PortfolioJobTypes.AnalyzePortfolioItem"/>
/// so the STOR-38 background worker can analyze it asynchronously. The two
/// commit-and-enqueue jobs run in one <c>SaveChangesAsync</c> call so a
/// half-committed state (item without its job, or vice versa) is impossible.
/// </para>
/// </remarks>
public class CreatePortfolioItemHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_FileSubmission_StreamsArtifactAndPersistsRow()
    {
        var accountContext = new AmbientAccountContext();
        var accountId = Guid.NewGuid();
        // The handler reads IAccountContext.UserId first (falling back to AccountId
        // only when UserId is null). Mirror what AccountContextMiddleware does in the
        // self-service path — populate both so the handler's null-check on UserId
        // doesn't throw.
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        await using var dbContext = CreateDbContext(accountContext);
        var artifactStore = new FakeArtifactStore();

        var file = new TestFormFile
        {
            FileName = "report.pdf",
            ContentType = "application/pdf",
            Length = 11, // 11 bytes
        };

        var request = new CreatePortfolioItemRequest
        {
            Label = "Final report",
            Category = PortfolioCategories.Document,
            CustomCategoryText = null,
            Description = "A summary.",
            ExternalUrl = null,
            File = file,
        };

        var response = await CreatePortfolioItemHandler.ExecuteAsync(
            request, dbContext, artifactStore, accountContext, CancellationToken.None);

        // Artifact side: exactly one PutAsync, with a server-generated key the
        // client filename is NOT part of.
        var put = Assert.Single(artifactStore.PutCalls);
        Assert.Equal("application/pdf", put.ContentType);
        Assert.Equal(11L, put.ByteCount);
        Assert.DoesNotContain("report.pdf", put.Key);
        Assert.StartsWith($"portfolio/{accountId:N}/", put.Key);

        // DB side: exactly one row, with file metadata and no URL.
        var stored = await dbContext.PortfolioItems.SingleAsync();
        Assert.Equal(accountId, stored.AccountId);
        Assert.Equal("Final report", stored.Label);
        Assert.Equal(PortfolioCategories.Document, stored.Category);
        Assert.Equal(PortfolioSubmissionTypes.File, stored.SubmissionType);
        Assert.NotNull(stored.StorageKey);
        Assert.Equal("report.pdf", stored.OriginalFileName);
        Assert.Equal("application/pdf", stored.ContentType);
        Assert.Equal(11L, stored.FileSizeBytes);
        Assert.Null(stored.ExternalUrl);

        // Response side: matches the DB row's id and excludes StorageKey (which
        // never leaves the server-side boundary, per PortfolioItemResponse's
        // remarks).
        Assert.Equal(stored.Id, response.Id);
        Assert.Equal(PortfolioSubmissionTypes.File, response.SubmissionType);

        // STOR-38 Phase 3 addendum: the create response carries the same
        // AnalysisStatus / LastAnalyzedAt fields the list endpoint surfaces. A
        // freshly created item is always NotAnalyzed with a null timestamp.
        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, response.AnalysisStatus);
        Assert.Null(response.LastAnalyzedAt);
    }

    [Fact]
    public async Task ExecuteAsync_LinkSubmission_SkipsStorageAndPersistsOnlyUrl()
    {
        var accountContext = new AmbientAccountContext();
        var accountId = Guid.NewGuid();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        await using var dbContext = CreateDbContext(accountContext);
        var artifactStore = new FakeArtifactStore();

        var request = new CreatePortfolioItemRequest
        {
            Label = "My portfolio",
            Category = PortfolioCategories.PortfolioLink,
            CustomCategoryText = null,
            Description = null,
            ExternalUrl = "https://example.com/portfolio",
            File = null,
        };

        var response = await CreatePortfolioItemHandler.ExecuteAsync(
            request, dbContext, artifactStore, accountContext, CancellationToken.None);

        Assert.Empty(artifactStore.PutCalls);

        var stored = await dbContext.PortfolioItems.SingleAsync();
        Assert.Equal(accountId, stored.AccountId);
        Assert.Equal(PortfolioSubmissionTypes.Link, stored.SubmissionType);
        Assert.Equal("https://example.com/portfolio", stored.ExternalUrl);
        Assert.Null(stored.StorageKey);
        Assert.Null(stored.OriginalFileName);
        Assert.Null(stored.FileSizeBytes);

        Assert.Equal(PortfolioSubmissionTypes.Link, response.SubmissionType);
        Assert.Equal("https://example.com/portfolio", response.ExternalUrl);

        // STOR-38 Phase 3 addendum: a fresh link submission's analysis state is
        // also surfaced on the create response — NotAnalyzed with no timestamp.
        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, response.AnalysisStatus);
        Assert.Null(response.LastAnalyzedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutAmbientAccount_Throws()
    {
        await using var dbContext = CreateDbContext(new AmbientAccountContext());
        var artifactStore = new FakeArtifactStore();
        var accountContext = new AmbientAccountContext(); // separate, no SetAccountId

        var request = new CreatePortfolioItemRequest
        {
            Label = "x",
            Category = PortfolioCategories.Document,
            File = new TestFormFile { FileName = "x.pdf", ContentType = "application/pdf", Length = 1 },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePortfolioItemHandler.ExecuteAsync(request, dbContext, artifactStore, accountContext, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_LinkSubmission_EnqueuesPendingAnalysisJobReferencingTheNewItem()
    {
        // The Phase 2 acceptance criterion: submitting a portfolio item via
        // CreatePortfolioItemHandler creates exactly one Job row with Type =
        // "AnalyzePortfolioItem", Status = "Pending", and a payload referencing
        // the new item's id. This test pins that contract directly against the
        // handler (no HTTP pipeline) so the enqueue is exercised on the same
        // WriteDbContext the rest of the unit suite already stands up.
        var accountContext = new AmbientAccountContext();
        var accountId = Guid.NewGuid();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        await using var dbContext = CreateDbContext(accountContext);
        var artifactStore = new FakeArtifactStore();

        var request = new CreatePortfolioItemRequest
        {
            Label = "Senior capstone",
            Category = PortfolioCategories.PortfolioLink,
            Description = "Built a Next.js dashboard.",
            ExternalUrl = "https://example.com/portfolio",
            File = null,
        };

        var response = await CreatePortfolioItemHandler.ExecuteAsync(
            request, dbContext, artifactStore, accountContext, CancellationToken.None);

        var job = await dbContext.Jobs.SingleAsync();
        Assert.Equal(PortfolioJobTypes.AnalyzePortfolioItem, job.Type);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(accountId, job.AccountId);
        Assert.Equal(0, job.AttemptCount);
        Assert.Null(job.StartedAt);
        Assert.Null(job.CompletedAt);

        // Payload is the AnalyzePortfolioItem payload type, deserialized back
        // into the same record the worker's processor will read.
        var payload = System.Text.Json.JsonSerializer.Deserialize<AnalyzePortfolioItemPayload>(job.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(response.Id, payload!.PortfolioItemId);
    }

    private static WriteDbContext CreateDbContext(AmbientAccountContext accountContext) =>
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
            accountContext);
}