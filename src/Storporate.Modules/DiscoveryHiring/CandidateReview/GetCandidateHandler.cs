using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.CandidateReview;

/// <summary>
/// Reads one <see cref="TalentIndexEntry"/> by id under the ambient Organization
/// account and projects it into the wire-shape
/// <see cref="CandidateResponse"/>. Returns <see cref="GetOutcome.NotFound"/>
/// when no row matches (the student opted out, or was never opted in) and the
/// endpoint maps to <c>404 candidate_not_found</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant isolation.</b> The non-tenant <see cref="TalentIndexEntry"/> table
/// has no RLS policy — that is by design (per the class remarks on
/// <see cref="TalentIndexEntry"/>) so any Organization scanning the search
/// results can read every opted-in student. The table is also not
/// <see cref="Storporate.SharedKernel.Entities.IAccountScoped"/>, so
/// <c>IgnoreQueryFilters()</c> is unnecessary — the global query filter
/// loop in <c>WriteDbContext.OnModelCreating</c> already skips the table.
/// The handler still uses <c>AsNoTracking()</c> because no EF write happens
/// here.
/// </para>
/// <para>
/// <b>Student-private tables are NEVER queried.</b> A query against
/// <c>PortfolioItems</c> or <c>StudentSearchProfiles</c> from this handler
/// would always return zero rows under the storporate_app role (the RLS
/// policy keys on the ambient <c>app.account_id</c> GUC, which is the
/// Organization's id — no match for any student's data). The handler's
/// contract — and the Live RLS test's assertion — is that the response is
/// built entirely from the <see cref="TalentIndexEntry"/> snapshot.
/// </para>
/// <para>
/// <b>Data rules.</b> An item is "shared" iff its snapshot carries a
/// non-null <see cref="TalentIndexOriginalSnapshot"/> (the per-item
/// descriptor the refresh processor copies into <c>ItemsJson</c> when the
/// student toggled <see cref="PortfolioItem.ShareOriginalWithEmployers"/>
/// on for that item; see STOR-44 Phase 1). When <see cref="TalentIndexOriginalSnapshot.StorageKey"/>
/// is non-blank the item is treated as a <see cref="TalentIndexOriginalKinds.File"/>
/// submission; a non-blank <see cref="TalentIndexOriginalSnapshot.Url"/>
/// is treated as a <see cref="TalentIndexOriginalKinds.Link"/>. AI reasons
/// (<see cref="TalentIndexSkillSnapshot.Reason"/>) and the
/// <see cref="CandidateOriginalResponse"/> are returned ONLY for shared
/// items — per the plan's data-rules section.
/// </para>
/// <para>
/// <b>Link URL safety.</b> A stored Link URL that does not parse via
/// <see cref="Uri.TryCreate(UriKind.Absolute)"/> with an http/https scheme
/// is treated as unavailable: the item surfaces as
/// <c>shared:false</c> and <c>original:null</c> here, and the original
/// endpoint returns <c>404 original_unavailable</c> for the same row. The
/// FE never gets to see the unsafe URL. Hosts are extracted from parsed
/// http/https URIs and are the only fragment of the URL the wire shape
/// exposes — the full URL is never returned.
/// </para>
/// <para>
/// <b>Audit.</b> On success the handler writes a <c>candidate_reviewed</c>
/// audit row with metadata <c>{candidateId}</c> ONLY — never the student's
/// account id, never the items, never the reasons, never the storage keys.
/// The metadata contract is pinned by the audit test (it does a literal
/// string search of <c>MetadataJson</c> against forbidden fragments).
/// </para>
/// </remarks>
public static class GetCandidateHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Outcome returned to the endpoint.
    /// <see cref="NotFound"/> maps to <c>404 candidate_not_found</c>; other
    /// shapes map to <c>200 OK</c> with <see cref="Response"/> as the body.
    /// </summary>
    public sealed record GetOutcome(
        bool NotFound,
        CandidateResponse? Response);

    public static async Task<GetOutcome> ExecuteAsync(
        Guid candidateId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(auditLogWriter);

        var entry = await dbContext.TalentIndexEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == candidateId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return new GetOutcome(NotFound: true, Response: null);
        }

        var items = DeserializeItems(entry.ItemsJson);
        var projectedItems = items
            .Select(ProjectItem)
            .ToList();

        var response = new CandidateResponse(
            CandidateId: entry.Id,
            DisplayName: entry.DisplayName,
            Headline: entry.Headline,
            University: entry.University,
            FieldOfStudy: entry.FieldOfStudy,
            StudyYear: entry.StudyYear,
            Items: projectedItems);

        // Audit metadata: candidateId ONLY. Plan rule — never the
        // student's account id, never items, never reasons, never the
        // storage keys or URLs. The audit test pins this contract with
        // a literal string search of MetadataJson.
        var auditMetadata = JsonSerializer.Serialize(
            new { candidateId },
            JsonOptions);
        await auditLogWriter.WriteAsync(
            action: "candidate_reviewed",
            resourceType: "TalentIndexEntry",
            resourceId: entry.Id.ToString(),
            metadataJson: auditMetadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new GetOutcome(NotFound: false, Response: response);
    }

    private static List<TalentIndexItemSnapshot> DeserializeItems(string itemsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TalentIndexItemSnapshot>>(
                itemsJson, JsonOptions) ?? new List<TalentIndexItemSnapshot>();
        }
        catch (JsonException)
        {
            // The blob was written by OUR refresh processor — it
            // should always round-trip. A failure here is the system
            // telling us the shape drifted (added a field the
            // deserializer doesn't recognize); return an empty items
            // list rather than 500 so a student with no items shows
            // the empty state instead of an error page.
            return new List<TalentIndexItemSnapshot>();
        }
    }

    /// <summary>Projects one snapshot item into the wire response. Implements
    /// the "shared" data rule: an item is shared iff its
    /// <see cref="TalentIndexItemSnapshot.Original"/> is non-null AND the
    /// descriptor is well-formed (a File has a non-blank storage key, or a
    /// Link parses as an absolute http/https URI). Reasons and the
    /// <see cref="CandidateOriginalResponse"/> surface ONLY for shared
    /// items — per the plan's data-rules section.</summary>
    private static CandidateItemResponse ProjectItem(TalentIndexItemSnapshot snapshot)
    {
        var shared = TryResolveShared(snapshot.Original, out var projectedOriginal);
        var skills = snapshot.Skills
            .Select(skill => new CandidateSkillResponse(
                Name: skill.Name,
                Band: skill.Band,
                Reason: shared ? skill.Reason : null))
            .ToList();
        return new CandidateItemResponse(
            PortfolioItemId: snapshot.PortfolioItemId,
            Label: snapshot.Label,
            Category: snapshot.Category,
            Shared: shared,
            Skills: skills,
            Original: projectedOriginal);
    }

    /// <summary>Returns <see langword="true"/> when the descriptor is a
    /// well-formed File (non-blank storage key) OR a Link whose URL parses
    /// as an absolute http/https URI. The host is extracted for the Link
    /// case; for a File the host is null.</summary>
    private static bool TryResolveShared(
        TalentIndexOriginalSnapshot? original,
        out CandidateOriginalResponse? projected)
    {
        projected = null;
        if (original is null)
        {
            return false;
        }

        if (string.Equals(original.Kind, TalentIndexOriginalKinds.File, StringComparison.Ordinal))
        {
            var hasStorageKey = !string.IsNullOrWhiteSpace(original.StorageKey);
            projected = new CandidateOriginalResponse(
                Kind: TalentIndexOriginalKinds.File,
                Available: hasStorageKey,
                FileName: original.FileName,
                ContentType: original.ContentType,
                SizeBytes: original.SizeBytes,
                Host: null);
            return hasStorageKey;
        }

        if (string.Equals(original.Kind, TalentIndexOriginalKinds.Link, StringComparison.Ordinal))
        {
            if (!IsHttpOrHttps(original.Url, out var host))
            {
                projected = null;
                return false;
            }

            projected = new CandidateOriginalResponse(
                Kind: TalentIndexOriginalKinds.Link,
                Available: true,
                FileName: null,
                ContentType: null,
                SizeBytes: null,
                Host: host);
            return true;
        }

        return false;
    }

    /// <summary>True iff <paramref name="value"/> parses via
    /// <see cref="Uri.TryCreate(UriKind.Absolute)"/> with an http or https
    /// scheme. <paramref name="host"/> is the URI's <see cref="Uri.Host"/>
    /// when the parse succeeds.</summary>
    private static bool IsHttpOrHttps(string? value, out string? host)
    {
        host = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        host = uri.Host;
        return true;
    }
}
