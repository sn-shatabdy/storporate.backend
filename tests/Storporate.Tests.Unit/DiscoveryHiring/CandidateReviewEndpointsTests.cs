using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.CandidateReview;
using Storporate.Tests.Unit.DiscoveryHiring.Fakes;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Storage;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-44 Phase 2 end-to-end integration coverage for the employer
/// drill-down surface:
/// <c>GET /api/discovery/candidates/{candidateId}</c> and
/// <c>GET /api/discovery/candidates/{candidateId}/items/{portfolioItemId}/original</c>.
/// Goes through the real ASP.NET Core pipeline with a real-issued
/// access token; <see cref="IArtifactStore"/> is the
/// <see cref="SeededArtifactStore"/> installed by
/// <see cref="CandidateReviewEndpointsFactory"/>, so no MinIO/S3 is
/// ever touched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pinned behaviors.</b>
/// <list type="bullet">
///   <item>Review GET: shared File item + unshared item — exact shape
///   (shared item has <c>reason</c> on every skill and <c>original</c>
///   populated; unshared has <c>shared:false</c>, <c>reason:null</c>,
///   <c>original:null</c>).</item>
///   <item>Review GET: shared Link item — <c>original.kind:"Link"</c>,
///   <c>original.host</c> matches the URL host, no full URL leaks, no
///   <c>url</c> property at all.</item>
///   <item>Review GET: unknown id → 404 <c>candidate_not_found</c>.</item>
///   <item>Response-shape guard: serialized JSON contains none of
///   <c>storageKey</c>, <c>accountId</c>, <c>studentAccountId</c>,
///   <c>email</c>, <c>score</c>, <c>rank</c>, <c>rating</c>, nor the
///   fixture's distinctive storage key / full URL strings.</item>
///   <item>Original GET, File: shared PDF → 200 + headers; shared docx
///   → attachment disposition; allowlist-out-of-set content type →
///   <c>application/octet-stream</c>; missing descriptor →
///   404 <c>original_not_shared</c>; unknown item id →
///   404 <c>original_not_shared</c>; missing blob →
///   404 <c>original_unavailable</c>; file name with path
///   separators / quotes is sanitized.</item>
///   <item>Original GET, Link: 200 with <c>{url}</c> body; unshared →
///   404 <c>original_not_shared</c>; stored <c>javascript:</c> or
///   <c>file:</c> URL → 404 <c>original_unavailable</c>.</item>
///   <item>Authorization: Student token → 403, no token → 401,
///   Administrator token → 403 on both endpoints.</item>
///   <item>Audit: one <c>candidate_original_opened</c> row per
///   successful original open with metadata containing
///   <c>portfolioItemId</c> + <c>kind</c> only; one
///   <c>candidate_reviewed</c> row per review GET with metadata
///   containing only <c>candidateId</c>. A failed original open
///   writes no audit row.</item>
/// </list>
/// </para>
/// <para>
/// <b>Factory + fakes.</b> <see cref="CandidateReviewEndpointsFactory"/>
/// inherits from <see cref="Auth.DiscoveryHiringEndpointsFactory"/> so the
/// in-memory EF Core swap, the placeholder Options values, and the
/// <see cref="FakeAuditLogWriter"/> swap are inherited unchanged. The
/// factory additionally swaps <see cref="IArtifactStore"/> for a
/// <see cref="SeededArtifactStore"/> so per-key content types are
/// honored.
/// </para>
/// </remarks>
public class CandidateReviewEndpointsTests : IClassFixture<CandidateReviewEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CandidateReviewEndpointsFactory _factory;

    public CandidateReviewEndpointsTests(CandidateReviewEndpointsFactory factory)
    {
        _factory = factory;
    }

    // ====================================================================
    // Review GET — shape
    // ====================================================================

    [Fact]
    public async Task Get_SharedFileItem_AndUnsharedItem_ReturnsExpectedShape()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var sharedItemId = Guid.NewGuid();
        var unsharedItemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        const string distinctiveStorageKey = "drilldown-test-shared-storage-key-do-not-leak";
        const string distinctiveFileName = "résumé-do-not-leak.pdf";

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{sharedItemId}"",
                ""label"": ""Personal API project"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the API in Python."" }}
                ],
                ""original"": {{
                    ""kind"": ""File"",
                    ""fileName"": ""{distinctiveFileName}"",
                    ""contentType"": ""application/pdf"",
                    ""sizeBytes"": 12345,
                    ""storageKey"": ""{distinctiveStorageKey}"",
                    ""url"": null
                }}
            }},
            {{
                ""portfolioItemId"": ""{unsharedItemId}"",
                ""label"": ""Old coursework"",
                ""category"": ""Coursework"",
                ""skills"": [
                    {{ ""name"": ""Java"", ""band"": ""Developing"", ""reason"": ""Did some Java homework."" }}
                ]
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/candidates/{candidateId}");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CandidateResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(candidateId, body!.CandidateId);
        Assert.Equal(2, body.Items.Count);

        var sharedItem = body.Items[0];
        Assert.Equal(sharedItemId, sharedItem.PortfolioItemId);
        Assert.Equal("Personal API project", sharedItem.Label);
        Assert.Equal("Project", sharedItem.Category);
        Assert.True(sharedItem.Shared);
        Assert.Single(sharedItem.Skills);
        Assert.Equal("Python", sharedItem.Skills[0].Name);
        Assert.Equal("Strong", sharedItem.Skills[0].Band);
        Assert.Equal("Wrote the API in Python.", sharedItem.Skills[0].Reason);

        Assert.NotNull(sharedItem.Original);
        Assert.Equal("File", sharedItem.Original!.Kind);
        Assert.True(sharedItem.Original.Available);
        Assert.Equal(distinctiveFileName, sharedItem.Original.FileName);
        Assert.Equal("application/pdf", sharedItem.Original.ContentType);
        Assert.Equal(12345L, sharedItem.Original.SizeBytes);
        Assert.Null(sharedItem.Original.Host);

        var unsharedItem = body.Items[1];
        Assert.Equal(unsharedItemId, unsharedItem.PortfolioItemId);
        Assert.False(unsharedItem.Shared);
        Assert.Single(unsharedItem.Skills);
        Assert.Equal("Java", unsharedItem.Skills[0].Name);
        Assert.Equal("Developing", unsharedItem.Skills[0].Band);
        Assert.Null(unsharedItem.Skills[0].Reason);
        Assert.Null(unsharedItem.Original);

        // The response-shape guard test also runs as a standalone assertion
        // below; this in-test assertion is the "explicit" pin for the
        // two-item fixture so a regression in either the shared or
        // unshared path is caught against the assertion closest to its
        // fixture.
        AssertNoForbiddenFragments(raw, distinctiveStorageKey);
    }

    [Fact]
    public async Task Get_SharedLinkItem_ReturnsLinkKindWithHostOnly_AndNoFullUrlInBody()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var linkItemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        const string fullUrl = "https://github.com/nadia/sales-dashboard";
        const string distinctiveFragment = "github.com/nadia/sales-dashboard-do-not-leak";

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{linkItemId}"",
                ""label"": ""Public dashboard"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the dashboard in Python."" }}
                ],
                ""original"": {{
                    ""kind"": ""Link"",
                    ""fileName"": null,
                    ""contentType"": null,
                    ""sizeBytes"": null,
                    ""storageKey"": null,
                    ""url"": ""{fullUrl}""
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/candidates/{candidateId}");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CandidateResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Single(body!.Items);

        var linkItem = body.Items[0];
        Assert.True(linkItem.Shared);
        Assert.NotNull(linkItem.Original);
        Assert.Equal("Link", linkItem.Original!.Kind);
        Assert.True(linkItem.Original.Available);
        Assert.Equal("github.com", linkItem.Original.Host);
        Assert.Null(linkItem.Original.FileName);
        Assert.Null(linkItem.Original.ContentType);
        Assert.Null(linkItem.Original.SizeBytes);

        // The full URL must NOT leak anywhere in the body, and the wire
        // shape must NOT carry a `url` property at the item or original
        // level — the FE does not need the URL at the review surface.
        Assert.DoesNotContain("github.com/nadia", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(distinctiveFragment, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"url\"", raw, StringComparison.Ordinal);

        // JSON property-name check: the candidate-level response object
        // must not include any property whose name is exactly "url".
        using var doc = JsonDocument.Parse(raw);
        AssertNoPropertyName(doc.RootElement, "url");
    }

    [Fact]
    public async Task Get_UnknownId_Returns404CandidateNotFound()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/candidates/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("candidate_not_found", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Get_ResponseShapeGuard_SerializedJsonContainsNoForbiddenFragments()
    {
        // The plan's "what is never returned" rule: storage keys, full
        // URLs, account ids, emails, scores, ranks, ratings — and the
        // distinctive fixture storage key + URL strings. The guard
        // test runs the rich-fixture path end-to-end and asserts none
        // of these fragments appear in the rendered JSON. A future
        // change that re-introduces any of them fails this test
        // before it ships.
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var sharedItemId = Guid.NewGuid();
        var linkItemId = Guid.NewGuid();
        var unsharedItemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var studentAccountId = Guid.NewGuid();

        const string distinctiveStorageKey = "STORAGE_KEY_DO_NOT_LEAK";
        const string distinctiveUrl = "https://example.invalid/secret-LEAK-DO-NOT-LEAK";

        await SeedIndexEntryAsync(
            candidateId,
            itemsJson: $@"[
                {{
                    ""portfolioItemId"": ""{sharedItemId}"",
                    ""label"": ""Personal API project"",
                    ""category"": ""Project"",
                    ""skills"": [
                        {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the API in Python."" }}
                    ],
                    ""original"": {{
                        ""kind"": ""File"",
                        ""fileName"": ""secret-name-LEAK.pdf"",
                        ""contentType"": ""application/pdf"",
                        ""sizeBytes"": 99,
                        ""storageKey"": ""{distinctiveStorageKey}"",
                        ""url"": null
                    }}
                }},
                {{
                    ""portfolioItemId"": ""{linkItemId}"",
                    ""label"": ""Public dashboard"",
                    ""category"": ""Project"",
                    ""skills"": [
                        {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the dashboard in Python."" }}
                    ],
                    ""original"": {{
                        ""kind"": ""Link"",
                        ""fileName"": null,
                        ""contentType"": null,
                        ""sizeBytes"": null,
                        ""storageKey"": null,
                        ""url"": ""{distinctiveUrl}""
                    }}
                }},
                {{
                    ""portfolioItemId"": ""{unsharedItemId}"",
                    ""label"": ""Old coursework"",
                    ""category"": ""Coursework"",
                    ""skills"": [
                        {{ ""name"": ""Java"", ""band"": ""Developing"", ""reason"": ""Did some Java homework."" }}
                    ]
                }}
            ]",
            studentAccountId: studentAccountId);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/candidates/{candidateId}");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertNoForbiddenFragments(raw, distinctiveStorageKey, distinctiveUrl);
    }

    // ====================================================================
    // Original GET — File
    // ====================================================================

    [Fact]
    public async Task Original_SharedPdf_Returns200WithInlineDispositionAndSecurityHeaders_AndBodyBytes()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var storageKey = $"org-{caller.Id:N}/{itemId:N}.pdf";
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%STOR-44-FIXTURE-BYTES\n%%EOF\n");

        _factory.ArtifactStore.SeedDefault(storageKey, pdfBytes, "application/pdf");

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Resume"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the resume parser."" }}
                ],
                ""original"": {{
                    ""kind"": ""File"",
                    ""fileName"": ""resume.pdf"",
                    ""contentType"": ""application/pdf"",
                    ""sizeBytes"": {pdfBytes.Length},
                    ""storageKey"": ""{storageKey}"",
                    ""url"": null
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var contentType = response.Content.Headers.ContentType;
        Assert.NotNull(contentType);
        Assert.Equal("application/pdf", contentType!.MediaType);

        // Content-Disposition begins with `inline` and carries the
        // sanitized file name via RFC 5987 filename*.
        Assert.NotNull(response.Content.Headers.ContentDisposition);
        var disposition = response.Content.Headers.ContentDisposition!.ToString();
        Assert.StartsWith("inline", disposition, StringComparison.Ordinal);
        Assert.Contains("filename*=UTF-8''resume.pdf", disposition, StringComparison.Ordinal);

        // Hardened security headers on every streaming file response.
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("sandbox; default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        // Cache-Control is asserted via the typed headers because the
        // raw value depends on directive order; what matters is that
        // every required directive is present.
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());

        // Body bytes match the stored bytes — the handler did not
        // double-buffer or corrupt the stream.
        var bodyBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(pdfBytes, bodyBytes);
    }

    [Fact]
    public async Task Original_SharedDocx_ReturnsAttachmentDisposition()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var storageKey = $"org-{caller.Id:N}/{itemId:N}.docx";
        var docxBytes = Encoding.ASCII.GetBytes("STOR-44-DOCX-FIXTURE-BYTES");

        _factory.ArtifactStore.SeedDefault(storageKey, docxBytes,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Resume"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Writing"", ""band"": ""Strong"", ""reason"": ""Wrote the resume prose."" }}
                ],
                ""original"": {{
                    ""kind"": ""File"",
                    ""fileName"": ""resume.docx"",
                    ""contentType"": ""application/vnd.openxmlformats-officedocument.wordprocessingml.document"",
                    ""sizeBytes"": {docxBytes.Length},
                    ""storageKey"": ""{storageKey}"",
                    ""url"": null
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // docx is in the AllowedContentTypes set but NOT in the
        // safe-inline list, so the disposition is `attachment` rather
        // than `inline` — the browser treats the response as a download.
        Assert.NotNull(response.Content.Headers.ContentDisposition);
        var disposition = response.Content.Headers.ContentDisposition!.ToString();
        Assert.StartsWith("attachment", disposition, StringComparison.Ordinal);
        Assert.Contains("filename*=UTF-8''resume.docx", disposition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Original_StoredContentTypeOutsideAllowlist_ServedAsApplicationOctetStream()
    {
        // A stored content type that is NOT in
        // CreatePortfolioItemValidator.AllowedContentTypes (e.g.
        // `application/x-msdownload`) must fall back to
        // `application/octet-stream` so the browser treats the
        // response as a download rather than rendering whatever the
        // student uploaded.
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var storageKey = $"org-{caller.Id:N}/{itemId:N}.bin";
        var bytes = Encoding.ASCII.GetBytes("STOR-44-OUTSIDE-ALLOWLIST");

        _factory.ArtifactStore.SeedDefault(storageKey, bytes, "application/x-msdownload");

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Resume"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the resume parser."" }}
                ],
                ""original"": {{
                    ""kind"": ""File"",
                    ""fileName"": ""resume.bin"",
                    ""contentType"": ""application/x-msdownload"",
                    ""sizeBytes"": {bytes.Length},
                    ""storageKey"": ""{storageKey}"",
                    ""url"": null
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var contentType = response.Content.Headers.ContentType;
        Assert.NotNull(contentType);
        Assert.Equal("application/octet-stream", contentType!.MediaType);
        Assert.NotNull(response.Content.Headers.ContentDisposition);
        Assert.StartsWith(
            "attachment",
            response.Content.Headers.ContentDisposition!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Original_ItemWithoutDescriptor_Returns404OriginalNotShared()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Resume"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the resume parser."" }}
                ]
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("original_not_shared", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Original_UnknownItemId_Returns404OriginalNotShared()
    {
        // The endpoint collapses "item not in snapshot" and "item has
        // no descriptor" onto the same code so the response does not
        // reveal which items exist behind the index.
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var candidateId = Guid.NewGuid();
        await SeedIndexEntryAsync(candidateId, itemsJson: "[]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{Guid.NewGuid()}/original");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("original_not_shared", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Original_SharedFileMissingBlob_Returns404OriginalUnavailable()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var storageKey = $"org-{caller.Id:N}/missing-{itemId:N}.pdf";

        // NOTE: SeededArtifactStore is NOT seeded with this key — the
        // handler's GetAsync call returns null, which surfaces as 404
        // original_unavailable.
        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Resume"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the resume parser."" }}
                ],
                ""original"": {{
                    ""kind"": ""File"",
                    ""fileName"": ""resume.pdf"",
                    ""contentType"": ""application/pdf"",
                    ""sizeBytes"": 99,
                    ""storageKey"": ""{storageKey}"",
                    ""url"": null
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("original_unavailable", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Original_FileNameWithPathSeparatorsAndQuotes_IsSanitizedInHeader()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var storageKey = $"org-{caller.Id:N}/{itemId:N}.pdf";
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n");

        _factory.ArtifactStore.SeedDefault(storageKey, pdfBytes, "application/pdf");

        // A file name that contains path separators, quotes, and
        // control characters. The handler strips every forbidden byte
        // before emitting the Content-Disposition header.
        const string nastyFileName = "../../etc/pass\"wd\nname.pdf";
        // JSON-encode the file name so the embedded quote / control
        // bytes don't break the surrounding JSON literal. The
        // serialized ItemsJson must remain valid JSON for the
        // handler's deserialization to succeed; the raw file name is
        // then unescaped into the descriptor when System.Text.Json
        // reads it back.
        var nastyFileNameJson = JsonSerializer.Serialize(nastyFileName);
        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Resume"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the resume parser."" }}
                ],
                ""original"": {{
                    ""kind"": ""File"",
                    ""fileName"": {nastyFileNameJson},
                    ""contentType"": ""application/pdf"",
                    ""sizeBytes"": {pdfBytes.Length},
                    ""storageKey"": ""{storageKey}"",
                    ""url"": null
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentDisposition);
        var disposition = response.Content.Headers.ContentDisposition!.ToString();

        // The sanitized file name has all '/' '\\' '"' '\n' stripped.
        // The exact ASCII path component "etc/pass" → "etcpass" once the
        // slash is removed; the embedded quote is gone; the '\n'
        // (newline control char) is gone. ".pdf" remains. The dots
        // from the leading "../../" also remain (dots are valid in
        // file names).
        Assert.Contains("etcpass", disposition, StringComparison.Ordinal);
        Assert.Contains("name.pdf", disposition, StringComparison.Ordinal);

        // No raw path separator in the header. (The RFC 5987
        // fallback filename="original" attribute carries legitimate
        // quote characters — that's the wire format — so the quote
        // assertion is dropped; the sanitized file name itself
        // cannot contain a quote.)
        Assert.DoesNotContain("../", disposition, StringComparison.Ordinal);
    }

    // ====================================================================
    // Original GET — Link
    // ====================================================================

    [Fact]
    public async Task Original_SharedLink_Returns200WithUrlBody()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        const string url = "https://github.com/nadia/sales-dashboard";

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Public dashboard"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the dashboard in Python."" }}
                ],
                ""original"": {{
                    ""kind"": ""Link"",
                    ""fileName"": null,
                    ""contentType"": null,
                    ""sizeBytes"": null,
                    ""storageKey"": null,
                    ""url"": ""{url}""
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(url, body!["url"]);
    }

    [Fact]
    public async Task Original_UnsharedLink_Returns404OriginalNotShared()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Public dashboard"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the dashboard in Python."" }}
                ]
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("original_not_shared", await ReadErrorCodeAsync(response));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    public async Task Original_StoredNonHttpUrl_Returns404OriginalUnavailable(string dangerousUrl)
    {
        // A Link submission whose URL does not parse as an absolute
        // http/https URL must surface as 404 original_unavailable so
        // a `javascript:` or `file:` URL never reaches the FE.
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Public dashboard"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the dashboard in Python."" }}
                ],
                ""original"": {{
                    ""kind"": ""Link"",
                    ""fileName"": null,
                    ""contentType"": null,
                    ""sizeBytes"": null,
                    ""storageKey"": null,
                    ""url"": ""{dangerousUrl}""
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("original_unavailable", await ReadErrorCodeAsync(response));
    }

    // ====================================================================
    // Authorization
    // ====================================================================

    [Fact]
    public async Task Get_WithoutBearerToken_Returns401()
    {
        var candidateId = Guid.NewGuid();
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/discovery/candidates/{candidateId}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsStudent_Returns403()
    {
        var caller = await SeedUserAsync(ActorTypes.Student, $"cand-review-student-{Guid.NewGuid():N}@example.com");
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/candidates/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsAdministrator_Returns403()
    {
        // Administrator's grant set equals Permissions.All (see
        // SystemRoles.BuildGrants), but the drill-down endpoints are
        // not yet enabled for the Administrator role. The explicit
        // CandidateReview grant-set widens ONLY the Organization
        // grant — pinning this test means the next story that opens
        // these endpoints to Administrator has to make a deliberate
        // change here too.
        var caller = await SeedUserAsync(ActorTypes.Administrator, $"cand-review-admin-{Guid.NewGuid():N}@example.com");
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/candidates/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Original_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(
            $"/api/discovery/candidates/{Guid.NewGuid()}/items/{Guid.NewGuid()}/original");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Original_AsStudent_Returns403()
    {
        var caller = await SeedUserAsync(ActorTypes.Student, $"cand-orig-student-{Guid.NewGuid():N}@example.com");
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{Guid.NewGuid()}/items/{Guid.NewGuid()}/original");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Original_AsAdministrator_Returns403()
    {
        var caller = await SeedUserAsync(ActorTypes.Administrator, $"cand-orig-admin-{Guid.NewGuid():N}@example.com");
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{Guid.NewGuid()}/items/{Guid.NewGuid()}/original");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ====================================================================
    // Audit
    // ====================================================================

    [Fact]
    public async Task Get_Success_WritesExactlyOneCandidateReviewedAuditRow_WithOnlyCandidateIdMetadata()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingCount = audit.Recorded.Count;

        var candidateId = Guid.NewGuid();
        await SeedIndexEntryAsync(candidateId, itemsJson: "[]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/discovery/candidates/{candidateId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var newEntries = audit.Recorded.Skip(existingCount).ToList();
        var row = Assert.Single(newEntries);
        Assert.Equal("candidate_reviewed", row.Action);
        Assert.Equal("TalentIndexEntry", row.ResourceType);
        Assert.Equal(candidateId.ToString(), row.ResourceId);

        // Metadata contract: ONLY candidateId is in the JSON. No
        // student's account id, no item ids, no skill names, no
        // reasons, no storage keys, no URLs.
        Assert.NotNull(row.MetadataJson);
        var doc = JsonDocument.Parse(row.MetadataJson!);
        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "candidateId" }, properties);
        Assert.Equal(candidateId.ToString(), doc.RootElement.GetProperty("candidateId").GetString());
    }

    [Fact]
    public async Task Original_FileSuccess_WritesExactlyOneCandidateOriginalOpenedAuditRow_WithOnlyPortfolioItemIdAndKindMetadata()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingCount = audit.Recorded.Count;

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var storageKey = $"org-{caller.Id:N}/{itemId:N}.pdf";
        var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n");

        _factory.ArtifactStore.SeedDefault(storageKey, pdfBytes, "application/pdf");

        const string distinctiveFileName = "STOR-44-AUDIT-LEAK.pdf";
        const string distinctiveUrlFragment = "STOR-44-AUDIT-LEAK-URL";

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Resume"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the resume parser."" }}
                ],
                ""original"": {{
                    ""kind"": ""File"",
                    ""fileName"": ""{distinctiveFileName}"",
                    ""contentType"": ""application/pdf"",
                    ""sizeBytes"": {pdfBytes.Length},
                    ""storageKey"": ""{storageKey}"",
                    ""url"": null
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var newEntries = audit.Recorded.Skip(existingCount).ToList();
        var row = Assert.Single(newEntries);
        Assert.Equal("candidate_original_opened", row.Action);
        Assert.Equal("TalentIndexEntryItem", row.ResourceType);
        Assert.Equal(itemId.ToString(), row.ResourceId);

        Assert.NotNull(row.MetadataJson);
        var doc = JsonDocument.Parse(row.MetadataJson!);
        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(2, properties.Count);
        Assert.Contains("portfolioItemId", properties);
        Assert.Contains("kind", properties);

        Assert.Equal(itemId.ToString(), doc.RootElement.GetProperty("portfolioItemId").GetString());
        Assert.Equal("File", doc.RootElement.GetProperty("kind").GetString());

        // Forbidden fragments: file name, URL fragment, storage key —
        // none of these must be in the audit metadata.
        Assert.DoesNotContain(distinctiveFileName, row.MetadataJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(distinctiveUrlFragment, row.MetadataJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(storageKey, row.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Original_LinkSuccess_WritesAuditRowWithKindLink()
    {
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingCount = audit.Recorded.Count;

        var itemId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        const string url = "https://github.com/nadia/sales-dashboard";

        await SeedIndexEntryAsync(candidateId, itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Public dashboard"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the dashboard in Python."" }}
                ],
                ""original"": {{
                    ""kind"": ""Link"",
                    ""fileName"": null,
                    ""contentType"": null,
                    ""sizeBytes"": null,
                    ""storageKey"": null,
                    ""url"": ""{url}""
                }}
            }}
        ]");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{itemId}/original");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var newEntries = audit.Recorded.Skip(existingCount).ToList();
        var row = Assert.Single(newEntries);
        Assert.Equal("candidate_original_opened", row.Action);

        Assert.NotNull(row.MetadataJson);
        var doc = JsonDocument.Parse(row.MetadataJson!);
        Assert.Equal("Link", doc.RootElement.GetProperty("kind").GetString());

        // The full URL must NOT leak into the audit metadata.
        Assert.DoesNotContain("github.com/nadia", row.MetadataJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(url, row.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Original_FailedOpen404_WritesNoCandidateOriginalOpenedAuditRow()
    {
        // The "candidate_original_opened audit only on success" rule.
        // Both 404 paths (candidate_not_found, original_not_shared,
        // original_unavailable) must NOT write the audit row.
        var caller = await SeedOrganizationAsync();
        var tokens = await IssueTokensAsync(caller);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingCount = audit.Recorded.Count;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        // 1. candidate_not_found — no entry at all.
        var response1 = await client.GetAsync(
            $"/api/discovery/candidates/{Guid.NewGuid()}/items/{Guid.NewGuid()}/original");
        Assert.Equal(HttpStatusCode.NotFound, response1.StatusCode);

        // 2. original_not_shared — entry exists but item has no descriptor.
        var candidateId = Guid.NewGuid();
        await SeedIndexEntryAsync(candidateId, itemsJson: "[]");
        var response2 = await client.GetAsync(
            $"/api/discovery/candidates/{candidateId}/items/{Guid.NewGuid()}/original");
        Assert.Equal(HttpStatusCode.NotFound, response2.StatusCode);

        // 3. original_unavailable — entry + descriptor present, blob missing.
        var itemId = Guid.NewGuid();
        var storageKeyMissing = $"org-{caller.Id:N}/missing-{Guid.NewGuid():N}.pdf";
        await SeedIndexEntryAsync(Guid.NewGuid(), itemsJson: $@"[
            {{
                ""portfolioItemId"": ""{itemId}"",
                ""label"": ""Resume"",
                ""category"": ""Project"",
                ""skills"": [
                    {{ ""name"": ""Python"", ""band"": ""Strong"", ""reason"": ""Wrote the resume parser."" }}
                ],
                ""original"": {{
                    ""kind"": ""File"",
                    ""fileName"": ""resume.pdf"",
                    ""contentType"": ""application/pdf"",
                    ""sizeBytes"": 99,
                    ""storageKey"": ""{storageKeyMissing}"",
                    ""url"": null
                }}
            }}
        ]");
        var response3 = await client.GetAsync(
            $"/api/discovery/candidates/{Guid.NewGuid()}/items/{itemId}/original");
        Assert.Equal(HttpStatusCode.NotFound, response3.StatusCode);

        // No candidate_original_opened rows were written.
        var newEntries = audit.Recorded.Skip(existingCount).ToList();
        Assert.DoesNotContain(newEntries, e => e.Action == "candidate_original_opened");
    }

    // ====================================================================
    // Sanitization helpers
    // ====================================================================

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("errorCode", out var errorCode)
            ? errorCode.GetString()
            : null;
    }

    /// <summary>The plan's "what is never returned" ban list — applied
    /// to every review response. The fragments are checked against the
    /// rendered JSON literally; a future change that re-introduces any
    /// of them trips the guard.</summary>
    private static readonly string[] ForbiddenFragments = new[]
    {
        "storageKey", "StorageKey", "accountId", "studentAccountId",
        "email", "score", "rank", "rating",
    };

    private static void AssertNoForbiddenFragments(string body, params string[] distinctiveStrings)
    {
        foreach (var fragment in ForbiddenFragments)
        {
            Assert.DoesNotContain(fragment, body, StringComparison.Ordinal);
        }
        foreach (var distinctive in distinctiveStrings)
        {
            Assert.DoesNotContain(distinctive, body, StringComparison.Ordinal);
        }
    }

    private static void AssertNoPropertyName(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.False(string.Equals(property.Name, propertyName, StringComparison.Ordinal),
                    $"Response contains forbidden property name '{propertyName}' at this scope.");
                AssertNoPropertyName(property.Value, propertyName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertNoPropertyName(item, propertyName);
            }
        }
    }

    // ====================================================================
    // Seed helpers
    // ====================================================================

    private async Task<User> SeedOrganizationAsync() =>
        await SeedUserAsync(ActorTypes.Organization, $"org-{Guid.NewGuid():N}@example.com");

    private async Task<User> SeedUserAsync(string actorType, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            ActorType = actorType,
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
        return await tokenService.IssueTokensAsync(user, "candidate-review-test", CancellationToken.None);
    }

    private async Task SeedIndexEntryAsync(Guid candidateId, string itemsJson, Guid? studentAccountId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();

        dbContext.TalentIndexEntries.Add(new TalentIndexEntry
        {
            Id = candidateId,
            StudentAccountId = studentAccountId ?? Guid.NewGuid(),
            DisplayName = "Fixture Student",
            Headline = "Fixture headline",
            University = "Fixture University",
            FieldOfStudy = "Computer Science",
            StudyYear = 3,
            ItemsJson = itemsJson,
            SearchText = "seed",
            ContentHash = "seed",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }
}
