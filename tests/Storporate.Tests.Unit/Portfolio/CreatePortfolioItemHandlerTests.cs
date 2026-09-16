using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
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

    private static WriteDbContext CreateDbContext(AmbientAccountContext accountContext) =>
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
            accountContext);
}