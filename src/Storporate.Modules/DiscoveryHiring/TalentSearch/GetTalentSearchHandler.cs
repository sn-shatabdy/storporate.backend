using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.TalentSearch;

/// <summary>
/// Reads a single <see cref="TalentSearchRequest"/> by id under the
/// ambient account and projects it into the wire-shape
/// <see cref="TalentSearchResponse"/>. On <see cref="TalentSearchStatuses.Completed"/>
/// the handler re-validates the stored results against the live
/// <see cref="TalentIndexEntry"/> snapshot so a student who hid or
/// edited their items since the search ran does not silently leak
/// stale values.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cross-account ids.</b> The lookup runs through the EF Core
/// global query filter on <see cref="IAccountScoped"/> so a
/// cross-account id never matches. The handler returns
/// <see cref="GetOutcome.NotFound"/> and the endpoint maps to
/// <c>404 talent_search_not_found</c> — never <c>403</c>, mirroring
/// the create handler / delete handler rule.
/// </para>
/// <para>
/// <b>Re-validation.</b> Three checks run on every completed GET:
/// <list type="number">
///   <item>The <see cref="TalentIndexEntry"/> for each
///   <see cref="TalentSearchResultItem.CandidateId"/> still exists.
///   If not (student hid their searchable profile), the result is
///   dropped from the array.</item>
///   <item>Each result's profile fields
///   (<see cref="TalentSearchResultItem.DisplayName"/>,
///   <c>Headline</c>, <c>University</c>, <c>FieldOfStudy</c>,
///   <c>StudyYear</c>) are refilled from the LIVE entry, not the
///   snapshot the processor wrote — the student may have changed
///   their visibility settings between then and now.</item>
///   <item>Each cited <see cref="TalentSearchCitedItem.PortfolioItemId"/>
///   still appears in the candidate's <see cref="TalentIndexEntry.ItemsJson"/>
///   AND its <see cref="TalentSearchCitedItem.SkillName"/> still matches
///   a skill of that item (case-insensitive). Stale citations are
///   dropped; an item that loses ALL its citations is dropped
///   entirely (a result with zero cited items would have its reason
///   replaced by the deterministic fallback the plan requires).</item>
/// </list>
/// </para>
/// <para>
/// <b>Status-gating.</b> <see cref="TalentSearchResponse.Results"/>
/// is <see langword="null"/> unless
/// <see cref="TalentSearchRequest.Status"/> is
/// <see cref="TalentSearchStatuses.Completed"/>. A row whose job is
/// still running (<see cref="TalentSearchStatuses.Pending"/>) or
/// whose processor has finished but marked it
/// <see cref="TalentSearchStatuses.Failed"/> returns just the status
/// + error code + timestamps.
/// </para>
/// </remarks>
public static class GetTalentSearchHandler
{
    /// <summary>The shape returned to the endpoint. Endpoint maps
    /// <see cref="NotFound"/> to <c>404 talent_search_not_found</c>.
    /// Other shapes always map to <c>200</c>.</summary>
    public sealed record GetOutcome(
        bool NotFound,
        TalentSearchResponse? Response);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<GetOutcome> ExecuteAsync(
        Guid searchId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // AsNoTracking + the global IAccountScoped query filter give
        // us a tenant-scoped read without the change-tracker overhead.
        var request = await dbContext.TalentSearchRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == searchId, cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return new GetOutcome(NotFound: true, Response: null);
        }

        // Pending + Failed short-circuit: no Results to project.
        if (request.Status != TalentSearchStatuses.Completed
            || string.IsNullOrEmpty(request.ResultJson))
        {
            return new GetOutcome(
                NotFound: false,
                Response: new TalentSearchResponse(
                    Id: request.Id,
                    Status: request.Status,
                    Query: request.QueryText,
                    CreatedAt: request.CreatedAt,
                    CompletedAt: request.CompletedAt,
                    Results: null,
                    ErrorCode: request.ErrorCode));
        }

        // Load + re-validate.
        var items = DeserializeResults(request.ResultJson);
        var validated = await RevalidateAsync(items, dbContext, cancellationToken)
            .ConfigureAwait(false);

        return new GetOutcome(
            NotFound: false,
            Response: new TalentSearchResponse(
                Id: request.Id,
                Status: request.Status,
                Query: request.QueryText,
                CreatedAt: request.CreatedAt,
                CompletedAt: request.CompletedAt,
                Results: validated,
                ErrorCode: null));
    }

    private static List<TalentSearchResultItem> DeserializeResults(string resultJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TalentSearchResultItem>>(
                resultJson, JsonOptions) ?? new List<TalentSearchResultItem>();
        }
        catch (JsonException)
        {
            // The blob was written by OUR processor — it should always
            // round-trip. A failure here is the system telling us the
            // shape drifted (added a field the deserializer doesn't
            // recognize); treat it as "no results yet" rather than a
            // 500 so the FE can poll again.
            return new List<TalentSearchResultItem>();
        }
    }

    /// <summary>Drop results whose <see cref="TalentIndexEntry"/> is gone,
    /// refill profile fields from the live entry, drop cited items no
    /// longer in the snapshot, and drop results that lose ALL their
    /// cited items. Returns the re-validated list.</summary>
    private static async Task<IReadOnlyList<TalentSearchResultItem>> RevalidateAsync(
        List<TalentSearchResultItem> items,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var candidateIds = items.Select(i => i.CandidateId).Distinct().ToList();
        // AsNoTracking + IgnoreQueryFilters so the non-tenant
        // TalentIndexEntry is readable; the table has no RLS policy.
        var entries = await dbContext.TalentIndexEntries
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(e => candidateIds.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var entriesById = entries.ToDictionary(e => e.Id);

        var output = new List<TalentSearchResultItem>(capacity: items.Count);
        foreach (var item in items)
        {
            if (!entriesById.TryGetValue(item.CandidateId, out var entry))
            {
                // The student removed themselves from the search index
                // (opted out, or deleted their portfolio) — drop the
                // entire result, not just the cited items.
                continue;
            }

            // Snapshot parsing for citation validation.
            IReadOnlyList<TalentIndexItemSnapshot> currentItems;
            try
            {
                currentItems = JsonSerializer.Deserialize<List<TalentIndexItemSnapshot>>(
                    entry.ItemsJson, JsonOptions) ?? new List<TalentIndexItemSnapshot>();
            }
            catch (JsonException)
            {
                // The snapshot is corrupted (should never happen — the
                // refresh processor writes it). Skip the candidate
                // rather than throw — the rest of the search still
                // goes out.
                continue;
            }
            var itemsById = currentItems.ToDictionary(i => i.PortfolioItemId);

            var validCitations = new List<TalentSearchCitedItem>();
            foreach (var citation in item.CitedItems)
            {
                if (!itemsById.TryGetValue(citation.PortfolioItemId, out var currentItem))
                {
                    // The item no longer appears in the snapshot — it was
                    // removed or re-analyzed and dropped from the index.
                    continue;
                }
                var matchedSkill = currentItem.Skills.FirstOrDefault(s =>
                    string.Equals(s.Name, citation.SkillName, StringComparison.OrdinalIgnoreCase));
                if (matchedSkill is null)
                {
                    // The skill name no longer matches anything on this
                    // item — the analysis was re-run.
                    continue;
                }
                validCitations.Add(new TalentSearchCitedItem(
                    PortfolioItemId: currentItem.PortfolioItemId,
                    Label: currentItem.Label,
                    Category: currentItem.Category,
                    SkillName: matchedSkill.Name,
                    Band: matchedSkill.Band));
            }

            // A result with zero cited items would have its reason
            // replaced by the deterministic fallback the plan mandates
            // for the processor. Doing that server-side here keeps the
            // FE simple: it never sees a result whose "show items" map
            // is empty.
            if (validCitations.Count == 0)
            {
                continue;
            }

            // Refill profile fields from the live entry — the student
            // may have flipped a Show flag since the search ran.
            output.Add(new TalentSearchResultItem(
                CandidateId: entry.Id,
                DisplayName: entry.DisplayName,
                Headline: entry.Headline,
                University: entry.University,
                FieldOfStudy: entry.FieldOfStudy,
                StudyYear: entry.StudyYear,
                MatchedSkills: item.MatchedSkills,
                Reason: item.Reason,
                CitedItems: validCitations));
        }

        return output;
    }
}
