using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;

/// <summary>
/// Company-side browse of Published club profiles (STOR-69). Only Published rows are ever
/// returned; the id in every response is the profile id, never the owner account id.
/// STOR-69 redo adds a <c>total</c> count to the list response so callers can render a
/// "showing N of total" affordance when the result is capped, and enriches the per-club
/// summary with founded year, audience study years, attendance min/max, and deduped support
/// needs.
/// </summary>
public static class BrowseClubsHandler
{
    public const int MaxResults = 50;

    public static async Task<ClubListResponse> ListAsync(
        string? query,
        string? field,
        string? university,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var profiles = dbContext.ClubProfiles
            .AsNoTracking()
            .Where(p => p.Status == ClubProfileStatuses.Published);

        var q = query?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(q))
        {
            profiles = profiles.Where(p =>
                p.Name.ToLower().Contains(q)
                || (p.Tagline != null && p.Tagline.ToLower().Contains(q))
                || p.University.ToLower().Contains(q));
        }

        var u = university?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(u))
        {
            profiles = profiles.Where(p => p.University.ToLower() == u);
        }

        var f = field?.Trim();
        if (!string.IsNullOrEmpty(f))
        {
            // The stored array holds JSON-escaped names, so match the JSON-encoded quoted term.
            var needle = JsonSerializer.Serialize(f, ClubProfileOptions.Json).ToLowerInvariant();
            profiles = profiles.Where(p => p.AudienceFieldsOfStudy.ToLower().Contains(needle));
        }

        // Total is computed BEFORE the Take(MaxResults) so the caller knows whether
        // the response was truncated. CountAsync with the filtered query runs one
        // SELECT against Postgres; cheap relative to the row read that follows.
        var total = await profiles.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await profiles
            .OrderByDescending(p => p.PublishedAt)
            .Take(MaxResults)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ClubListResponse(rows.Select(ToSummary).ToList(), total);
    }

    public static async Task<ClubProfileResponse?> GetAsync(
        Guid id,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.ClubProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.Status == ClubProfileStatuses.Published, cancellationToken)
            .ConfigureAwait(false);
        return profile is null ? null : ManageClubProfileHandler.ToResponse(profile);
    }

    internal static ClubSummaryResponse ToSummary(ClubProfile p)
    {
        var fields = ClubProfileOptions.Deserialize<List<string>>(p.AudienceFieldsOfStudy, []);
        var years = ClubProfileOptions.Deserialize<List<int>>(p.AudienceYears, []);
        var events = ClubProfileOptions.Deserialize<List<ClubEvent>>(p.EventsJson, []);

        return new ClubSummaryResponse(
            p.Id,
            p.Name,
            p.Tagline,
            p.University,
            p.MemberCount,
            fields,
            events.Count,
            p.FoundedYear,
            years,
            SummarizeAttendance(events),
            NormalizeSupportNeeds(events));
    }

    /// <summary>Min/max typical attendance across the club's events. Both null when no event carries a value.</summary>
    private static ClubEventAttendanceSummary SummarizeAttendance(IReadOnlyList<ClubEvent> events)
    {
        if (events.Count == 0)
        {
            return new ClubEventAttendanceSummary(null, null);
        }

        int? min = null;
        int? max = null;
        foreach (var e in events)
        {
            // Every event today stores a non-null attendance (the validator requires it on save);
            // the null branch stays as a future-proof guard so a partial row doesn't crash browse.
            var attendance = e.TypicalAttendance;
            if (min is null || attendance < min)
            {
                min = attendance;
            }

            if (max is null || attendance > max)
            {
                max = attendance;
            }
        }

        return new ClubEventAttendanceSummary(min, max);
    }

    /// <summary>Union of every event's support needs, deduped case-insensitively, trimmed,
    /// blanks dropped, first spelling wins, sorted ascending. Same shape as
    /// <c>JobPostingSkills.Normalize</c> in the DiscoveryHiring module.</summary>
    private static IReadOnlyList<string> NormalizeSupportNeeds(IReadOnlyList<ClubEvent> events)
    {
        if (events.Count == 0)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var e in events)
        {
            foreach (var raw in e.SupportNeeds ?? Array.Empty<string>())
            {
                var trimmed = raw?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
