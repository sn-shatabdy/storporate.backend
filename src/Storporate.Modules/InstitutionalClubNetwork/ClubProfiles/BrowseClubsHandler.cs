using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;

/// <summary>
/// Company-side browse of Published club profiles (STOR-69). Only Published rows are ever
/// returned; the id in every response is the profile id, never the owner account id.
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

        var rows = await profiles
            .OrderByDescending(p => p.PublishedAt)
            .Take(MaxResults)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ClubListResponse(rows.Select(ToSummary).ToList());
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

    private static ClubSummaryResponse ToSummary(ClubProfile p) => new(
        p.Id,
        p.Name,
        p.Tagline,
        p.University,
        p.MemberCount,
        ClubProfileOptions.Deserialize<List<string>>(p.AudienceFieldsOfStudy, []),
        ClubProfileOptions.Deserialize<List<ClubEvent>>(p.EventsJson, []).Count);
}
