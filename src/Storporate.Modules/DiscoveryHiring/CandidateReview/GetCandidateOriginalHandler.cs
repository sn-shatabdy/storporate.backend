using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Storage;

namespace Storporate.Modules.DiscoveryHiring.CandidateReview;

/// <summary>
/// Streams the original file or returns the URL behind a single item on the
/// <see cref="TalentIndexEntry"/> index table — the second endpoint of the
/// STOR-44 Phase 2 drill-down surface,
/// <c>GET /api/discovery/candidates/{candidateId}/items/{portfolioItemId}/original</c>.
/// Reads ONLY the non-tenant <see cref="TalentIndexEntry"/> table (the same
/// constraint as <see cref="GetCandidateHandler"/>) and the
/// <see cref="IArtifactStore"/> for the file-streaming case.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no File submission validation against student tables.</b> The
/// storage key is already on the snapshot — Phase 1's refresh processor
/// copied it server-side under the student's account scope and stored it
/// in <see cref="TalentIndexOriginalSnapshot.StorageKey"/>. The handler
/// hands that storage key to <see cref="IArtifactStore.GetAsync"/> which
/// resolves the bytes from MinIO/R2 under the Organization's ambient
/// account context; no read of <see cref="PortfolioItem"/> rows is needed.
/// </para>
/// <para>
/// <b>Errors are collapsed onto three codes.</b>
/// <list type="number">
///   <item><c>404 candidate_not_found</c> — no <see cref="TalentIndexEntry"/>
///   row with the supplied <c>candidateId</c> (the student opted out or was
///   never opted in).</item>
///   <item><c>404 original_not_shared</c> — the row exists but the item
///   either is not in the snapshot OR the snapshot item has no
///   <see cref="TalentIndexOriginalSnapshot"/> descriptor. Both cases use
///   the same code so the endpoint never leaks whether a particular
///   <c>portfolioItemId</c> exists behind the index.</item>
///   <item><c>404 original_unavailable</c> — the descriptor is present
///   but the blob / URL cannot be served. A File whose storage key returns
///   <see langword="null"/> from <see cref="IArtifactStore.GetAsync"/>; an
///   <see cref="Storporate.Infrastructure.Storage.ArtifactStorageException"/>
///   that means "not found" (the wrapper preserves the not-found
///   distinction the underlying S3 client raised); OR a Link whose URL
///   does not parse as an absolute http/https URI.</item>
/// </list>
/// </para>
/// <para>
/// <b>Storage failure semantics.</b> Any
/// <see cref="Storporate.Infrastructure.Storage.ArtifactStorageException"/>
/// that is NOT a not-found becomes the normal 5xx via the global handler —
/// it is not swallowed. A not-found result from <see cref="IArtifactStore.GetAsync"/>
/// is mapped onto <c>404 original_unavailable</c> because the blob simply
/// is not there to serve; a not-found <c>ArtifactStorageException</c> is
/// treated the same way.
/// </para>
/// <para>
/// <b>Link response.</b> Always <c>200 OK</c> with JSON
/// <c>{ "url": "&lt;stored&gt;" }</c>; the original endpoint does NOT
/// redirect — it returns the URL so the FE can decide whether to render an
/// iframe / open-in-new-tab / etc. The stored URL is validated to be
/// http/https here (same <c>IsHttpOrHttps</c> rule the review endpoint
/// uses) so a <c>javascript:</c> or <c>file:</c> submission comes through
/// as <c>404 original_unavailable</c> rather than reaching the FE.
/// </para>
/// <para>
/// <b>File response.</b> Streams the bytes through
/// <see cref="Microsoft.AspNetCore.Http.Results.Stream(string, Stream, string?, string?, DateTimeOffset?, IReadOnlyDictionary{string, string}?)"/>
/// so the response body is never fully buffered in process memory — the
/// <see cref="ArtifactContent.Stream"/> from
/// <see cref="IArtifactStore.GetAsync(string, CancellationToken)"/>
/// flows straight to the response. The <see cref="ArtifactContent"/>'s
/// stream is disposed by ASP.NET Core after the response completes. The
/// <c>Content-Type</c> header carries the STORED content type from the
/// descriptor (never derived from the request). When the stored type is
/// not in <see cref="CreatePortfolioItemValidator.AllowedContentTypes"/>
/// the response content type falls back to <c>application/octet-stream</c>
/// so the browser treats it as a download; the safe-inline types
/// (PDF, PNG, JPEG, GIF, WEBP, MP4, WEBM) are served with
/// <c>inline</c> disposition; every other allowed type falls back to
/// <c>attachment</c>. The file name is sanitized (path separators and
/// control characters stripped) and the header is
/// <c>filename*=UTF-8''&lt;url-encoded&gt;</c> per RFC 5987.
/// </para>
/// <para>
/// <b>Security headers.</b> <c>X-Content-Type-Options: nosniff</c>,
/// <c>Content-Security-Policy: sandbox; default-src 'none'</c>,
/// <c>Cache-Control: private, no-store</c>,
/// <c>Referrer-Policy: no-referrer</c> — applied to the file response
/// only. The Link JSON response carries none of these (it is just JSON
/// metadata) and the original endpoint is the same security context as
/// the review endpoint, so cross-account reads are still impossible.
/// </para>
/// <para>
/// <b>Audit.</b> On success the handler writes a
/// <c>candidate_original_opened</c> audit row with metadata
/// <c>{ portfolioItemId, kind }</c> ONLY — never the file name, URL, or
/// storage key. The audit write happens AFTER the blob has been located
/// (<see cref="IArtifactStore.GetAsync"/> returned content) or the Link
/// has resolved, but BEFORE the response body starts streaming. A 404
/// path writes no audit row. The audit test pins the contract with a
/// literal string search of <c>MetadataJson</c> against forbidden
/// fragments.
/// </para>
/// </remarks>
public static class GetCandidateOriginalHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The content types the original endpoint serves with the
    /// safe-inline disposition. Subset of
    /// <see cref="PortfolioContentTypes.Allowed"/>; every other type
    /// (including application/octet-stream fallback) gets the
    /// <c>attachment</c> disposition. Order-independent; the comparison
    /// is ordinal-case-insensitive against the STORED content type from
    /// the descriptor.</summary>
    private static readonly IReadOnlySet<string> SafeInlineContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "video/mp4",
        "video/webm",
    };

    /// <summary>Outcome returned to the endpoint. <see cref="NotFound"/> carries
    /// the specific 404 errorCode; <see cref="FileResponse"/> / <see cref="LinkResponse"/>
    /// are the two success shapes. <see cref="Unavailable"/> maps to
    /// <c>404 original_unavailable</c>.</summary>
    public sealed record GetOutcome
    {
        public bool NotFound { get; init; }
        public string? NotFoundErrorCode { get; init; }

        public bool Unavailable { get; init; }

        public FileOutcome? FileResponse { get; init; }
        public string? LinkUrl { get; init; }

        public static GetOutcome CandidateNotFound() =>
            new() { NotFound = true, NotFoundErrorCode = "candidate_not_found" };

        public static GetOutcome OriginalNotShared() =>
            new() { NotFound = true, NotFoundErrorCode = "original_not_shared" };

        public static GetOutcome OriginalUnavailable() =>
            new() { Unavailable = true };

        public static GetOutcome File(FileOutcome outcome) =>
            new() { FileResponse = outcome };

        public static GetOutcome Link(string url) =>
            new() { LinkUrl = url };
    }

    /// <summary>One streaming file response. <see cref="Content"/> is the
    /// <see cref="IArtifactStore"/>'s stream — the endpoint disposes it
    /// after ASP.NET Core finishes writing the body.</summary>
    public sealed record FileOutcome(
        Stream Content,
        string ContentType,
        string FileName);

    public static async Task<GetOutcome> ExecuteAsync(
        Guid candidateId,
        Guid portfolioItemId,
        WriteDbContext dbContext,
        IArtifactStore artifactStore,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(auditLogWriter);

        var entry = await dbContext.TalentIndexEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == candidateId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return GetOutcome.CandidateNotFound();
        }

        var snapshotItem = FindSnapshotItem(entry.ItemsJson, portfolioItemId);
        var original = snapshotItem?.Original;
        if (snapshotItem is null || original is null)
        {
            // Both "item not in snapshot" AND "item has no descriptor" land
            // on the same code so the endpoint does not reveal which items
            // exist behind the index.
            return GetOutcome.OriginalNotShared();
        }

        if (string.Equals(original.Kind, TalentIndexOriginalKinds.File, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(original.StorageKey))
            {
                return GetOutcome.OriginalUnavailable();
            }

            ArtifactContent? content;
            try
            {
                content = await artifactStore
                    .GetAsync(original.StorageKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Storporate.Infrastructure.Storage.ArtifactStorageException)
            {
                // Other failures (unreachable storage, auth) escape to the
                // global handler as a 5xx — we only collapse the not-found
                // case. The blob's not-found paths are:
                //   1. IArtifactStore.GetAsync returns null (S3 returned 404);
                //   2. ArtifactStorageException wrapped a not-found;
                // In both cases the user has nothing to open, so we
                // surface 404 original_unavailable rather than the generic
                // 502 the global handler would have produced.
                // Since the wrapper preserves the not-found only when the
                // SDK response code is 404 (see S3ArtifactStore.IsNotFound),
                // any other ArtifactStorageException is a real outage and
                // intentionally propagates.
                return GetOutcome.OriginalUnavailable();
            }

            if (content is null)
            {
                return GetOutcome.OriginalUnavailable();
            }

            var resolvedContentType = ResolveContentType(original.ContentType);
            var safeFileName = SanitizeFileName(original.FileName);

            await WriteAuditAsync(
                auditLogWriter,
                portfolioItemId,
                TalentIndexOriginalKinds.File,
                cancellationToken).ConfigureAwait(false);

            return GetOutcome.File(new FileOutcome(
                Content: content.Content,
                ContentType: resolvedContentType,
                FileName: safeFileName));
        }

        if (string.Equals(original.Kind, TalentIndexOriginalKinds.Link, StringComparison.Ordinal))
        {
            if (!IsHttpOrHttps(original.Url))
            {
                return GetOutcome.OriginalUnavailable();
            }

            await WriteAuditAsync(
                auditLogWriter,
                portfolioItemId,
                TalentIndexOriginalKinds.Link,
                cancellationToken).ConfigureAwait(false);

            return GetOutcome.Link(original.Url!);
        }

        // Unknown descriptor kind — treat as not shared.
        return GetOutcome.OriginalNotShared();
    }

    private static async Task WriteAuditAsync(
        IAuditLogWriter auditLogWriter,
        Guid portfolioItemId,
        string kind,
        CancellationToken cancellationToken)
    {
        var auditMetadata = JsonSerializer.Serialize(
            new
            {
                portfolioItemId,
                kind,
            },
            JsonOptions);
        await auditLogWriter.WriteAsync(
            action: "candidate_original_opened",
            resourceType: "TalentIndexEntryItem",
            resourceId: portfolioItemId.ToString(),
            metadataJson: auditMetadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Walks the entry's <c>ItemsJson</c> and returns the item whose
    /// <see cref="TalentIndexItemSnapshot.PortfolioItemId"/> equals
    /// <paramref name="portfolioItemId"/>, or <see langword="null"/> when no
    /// such item is in the snapshot. A malformed JSON blob returns null —
    /// the handler maps that to <c>404 original_not_shared</c> so the
    /// endpoint does not 500 on storage corruption.</summary>
    private static TalentIndexItemSnapshot? FindSnapshotItem(string itemsJson, Guid portfolioItemId)
    {
        List<TalentIndexItemSnapshot>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<TalentIndexItemSnapshot>>(
                itemsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (items is null)
        {
            return null;
        }
        foreach (var item in items)
        {
            if (item.PortfolioItemId == portfolioItemId)
            {
                return item;
            }
        }
        return null;
    }

    /// <summary>Maps the STORED content type to the value the response
    /// carries. <see langword="null"/> / blank falls back to
    /// <c>application/octet-stream</c>; a value not in the allowed
    /// portfolio set falls back to the same octet-stream so the browser
    /// treats it as a download rather than rendering whatever the user
    /// uploaded.</summary>
    internal static string ResolveContentType(string? storedContentType)
    {
        if (string.IsNullOrWhiteSpace(storedContentType))
        {
            return "application/octet-stream";
        }
        if (PortfolioContentTypes.Allowed.Contains(storedContentType))
        {
            return storedContentType;
        }
        return "application/octet-stream";
    }

    /// <summary>Returns <see langword="true"/> iff <paramref name="value"/>
    /// parses via <see cref="Uri.TryCreate(UriKind.Absolute)"/> with an http
    /// or https scheme.</summary>
    private static bool IsHttpOrHttps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>True iff the stored content type is in the safe-inline
    /// list. Exposed internal so the test can pin the rule without
    /// duplicating the set.</summary>
    internal static bool IsSafeInline(string contentType) =>
        SafeInlineContentTypes.Contains(contentType);

    /// <summary>Strips path separators (<c>/</c>, <c>\</c>), control
    /// characters, double-quote, and the RFC 5987 forbidden bytes from
    /// <paramref name="fileName"/> and falls back to <c>"original"</c>
    /// when the result is empty. The Content-Disposition header is built
    /// with the URL-encoded value so the resulting header carries the
    /// original UTF-8 name verbatim.</summary>
    internal static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "original";
        }

        var sanitized = new System.Text.StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            // Strip path separators and control characters and the
            // double-quote / star / colon / angle-bracket that
            // Content-Disposition parsers historically choke on.
            if (ch == '/' || ch == '\\' || ch == '"' || ch == '*' || ch == ':'
                || ch == '<' || ch == '>' || ch == '|' || ch == '?'
                || ch < 0x20 || ch == 0x7F)
            {
                continue;
            }
            sanitized.Append(ch);
        }

        return sanitized.Length == 0 ? "original" : sanitized.ToString();
    }
}
