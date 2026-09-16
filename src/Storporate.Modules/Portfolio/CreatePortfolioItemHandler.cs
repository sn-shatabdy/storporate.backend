using System.Text.Json;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio.Analysis;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Storage;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Persists a single <see cref="PortfolioItem"/> row for the caller's account. Branches
/// on <see cref="PortfolioSubmissionTypes"/>: a file submission streams the upload to
/// <see cref="IArtifactStore"/> under a server-generated key (never the client's
/// filename — see <see cref="GenerateStorageKey"/>), then writes the DB row carrying
/// <c>StorageKey</c>/<c>OriginalFileName</c>/<c>ContentType</c>/<c>FileSizeBytes</c>;
/// a link submission skips storage entirely and writes only the <c>ExternalUrl</c>.
/// </summary>
/// <remarks>
/// <para>
/// The validator has already enforced exactly-one-of (file, url) by the time this
/// handler runs, so the <c>if (request.File is not null)</c> branch is the
/// dispatch source of truth on the success path — there is no separate "what kind of
/// submission is this" field in the request.
/// </para>
/// <para>
/// The caller account is resolved from the ambient <see cref="IAccountContext"/>.
/// <see cref="IAccountContext.AccountId"/> wins when set (the cross-account
/// admin case where an endpoint binds <c>{accountId}</c>); otherwise the
/// self-service case resolves to <see cref="IAccountContext.UserId"/> because
/// <see cref="AccountContextMiddleware"/> falls back to the JWT subject for
/// endpoints that don't bind <c>{accountId}</c>. <see cref="PortfolioItem.AccountId"/>
/// is set from this resolved value directly rather than trusting any
/// client-supplied account id; the save-time <c>RowLevelSecurityInterceptor</c>
/// validates the same value against the ambient context again on
/// <c>SaveChangesAsync</c>, so a manipulated value would fail loudly at the DB
/// rather than silently leak across accounts.
/// </para>
/// <para>
/// If the storage write fails after the validator passed, the DB row is never created
/// because the storage call runs first and throws on its own. The exception
/// propagates up to the global exception handler, which maps
/// <see cref="Infrastructure.Storage.ArtifactStorageException"/> to a 502.
/// </para>
/// </remarks>
public static class CreatePortfolioItemHandler
{
    public static async Task<CreatePortfolioItemResponse> ExecuteAsync(
        CreatePortfolioItemRequest request,
        WriteDbContext dbContext,
        IArtifactStore artifactStore,
        IAccountContext accountContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(accountContext);

        if (!accountContext.UserId.HasValue)
        {
            // No ambient user — should be unreachable when the endpoint is
            // correctly registered with .RequireAuthorization() because the JWT
            // bearer middleware populates IAccountContext from the principal before
            // the handler runs. Throw rather than defaulting so a misconfigured
            // pipeline fails loudly, not silently.
            throw new InvalidOperationException(
                "Cannot create a portfolio item without an ambient account context.");
        }

        // Cross-account endpoints bind {accountId} and that value wins; self-service
        // endpoints fall back to the JWT subject so workspace == account per the
        // STOR-62 plan's "Context & Findings" rule.
        var accountId = accountContext.AccountId ?? accountContext.UserId.Value;
        var now = DateTimeOffset.UtcNow;

        string? storageKey = null;
        string? originalFileName = null;
        string? contentType = null;
        long? fileSizeBytes = null;
        string submissionType;

        if (request.File is not null)
        {
            submissionType = PortfolioSubmissionTypes.File;
            storageKey = GenerateStorageKey(accountId, request.File.FileName, request.File.ContentType);
            originalFileName = request.File.FileName;
            contentType = request.File.ContentType;
            fileSizeBytes = request.File.Length;

            // OpenReadStream() returns the buffered temp stream the multipart
            // binder already produced; CopyToAsync streams it straight to S3/MinIO
            // without buffering the bytes a second time in memory.
            await using var uploadStream = request.File.OpenReadStream();
            await artifactStore.PutAsync(
                key: storageKey,
                content: uploadStream,
                contentType: contentType,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            submissionType = PortfolioSubmissionTypes.Link;
        }

        var portfolioItem = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Label = request.Label!,
            Category = request.Category!,
            CustomCategoryText = request.CustomCategoryText,
            SubmissionType = submissionType,
            StorageKey = storageKey,
            OriginalFileName = originalFileName,
            ContentType = contentType,
            FileSizeBytes = fileSizeBytes,
            ExternalUrl = request.ExternalUrl,
            Description = request.Description,
            CreatedAt = now,
        };

        dbContext.PortfolioItems.Add(portfolioItem);

        // STOR-38 Phase 2: every accepted submission immediately enqueues an analysis
        // job for the STOR-38 worker. The job's AccountId mirrors the portfolio item's
        // so the worker's row-claim query (filtered by AccountId via the global query
        // filter + by Type) sees the row under the same tenancy gate as the item itself.
        // Both writes commit in one SaveChanges call so a half-committed state (item
        // without its analysis job, or vice versa) is impossible — a transient failure
        // on the second call would previously leave an orphaned NotAnalyzed item that
        // no retry path could rescue (retry only works from Failed). portfolioItem.Id
        // is set eagerly above so the job's PayloadJson can reference it independently
        // of save-call ordering.
        var analysisJob = new Job
        {
            Id = Guid.NewGuid(),
            Type = PortfolioJobTypes.AnalyzePortfolioItem,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            PayloadJson = JsonSerializer.Serialize(
                new AnalyzePortfolioItemPayload(portfolioItem.Id)),
            AccountId = accountId,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        };
        dbContext.Jobs.Add(analysisJob);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return CreatePortfolioItemResponse.FromEntity(portfolioItem);
    }

    /// <summary>
    /// Produces a server-side storage key the client cannot influence. The shape
    /// (<c>portfolio/{accountId}/{guid}.{sanitized-extension-or-none}</c>) matches
    /// the convention <see cref="Storporate.Modules.PlatformFoundations.Diagnostics.StoragePingHandler"/>
    /// uses for diagnostics keys (<c>diagnostics/ping-{guid}.txt</c>) — a
    /// content-type-derived or absent extension, a guid for uniqueness, and a
    /// per-account prefix so a future cleanup-by-account job can sweep a tenant's
    /// blos with a single prefix query.
    /// </summary>
    private static string GenerateStorageKey(Guid accountId, string originalFileName, string contentType)
    {
        // Derive the extension from the validated content type, NOT from the
        // user-supplied filename — see minimal-api-file-upload skill step 4
        // "Deriving file extension from user input: prefer deriving the extension
        // from the validated content type". For simplicity here we keep the
        // extension as just whatever the content type suggests; a future story
        // can swap in a full content-type → extension map if needed.
        var extension = ContentTypeToExtension(contentType);
        var guid = Guid.NewGuid();
        return extension is null
            ? $"portfolio/{accountId:N}/{guid}"
            : $"portfolio/{accountId:N}/{guid}{extension}";
    }

    private static string? ContentTypeToExtension(string contentType) =>
        contentType.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "application/msword" => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.ms-powerpoint" => ".ppt",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "text/plain" => ".txt",
            "text/csv" => ".csv",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/webm" => ".webm",
            "application/zip" => ".zip",
            _ => null,
        };
}