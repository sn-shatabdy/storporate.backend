using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;
using Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipMatching;

/// <summary>
/// STOR-71 read-only matching, computed on the fly from existing tables (no new data). A company
/// sees Published clubs that fit one of its own goal sets; a club sees Active goal sets that fit its
/// own Published profile. Every match carries a fit word and plain reasons; points stay internal.
/// No owner id, email or account data is ever returned.
/// </summary>
public static class SponsorshipMatchHandler
{
    public const int MaxResults = 30;

    // Upper bound on candidates read into memory per call (newest first).
    public const int MaxCandidates = 500;

    public static async Task<MatchOutcome<ClubMatchListResponse>> ClubsForGoalAsync(
        Guid goalId,
        Guid accountId,
        string? query,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseQuery(query, out var parsed))
        {
            return MatchOutcome<ClubMatchListResponse>.Fail(SponsorshipMatchErrors.QueryInvalid);
        }

        var goalSet = await dbContext.SponsorshipGoalSets
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == goalId && s.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (goalSet is null)
        {
            return MatchOutcome<ClubMatchListResponse>.Fail(SponsorshipMatchErrors.GoalNotFound);
        }

        var goal = ToMatchGoal(goalSet);
        var clubs = await dbContext.ClubProfiles
            .AsNoTracking()
            .Where(p => p.Status == ClubProfileStatuses.Published)
            .OrderByDescending(p => p.PublishedAt)
            .ThenByDescending(p => p.Id)
            .Take(MaxCandidates)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ranked = new List<Ranked<ClubMatch>>();
        foreach (var profile in clubs)
        {
            var club = ToMatchClub(profile);
            var assessed = Evaluate(parsed, goal, club, SearchableText(club), MatchPerspective.Company);
            if (assessed is { } a)
            {
                ranked.Add(new Ranked<ClubMatch>(a.Band, a.Points, new ClubMatch(
                    a.Band.ToString(), a.Reasons, BrowseClubsHandler.ToSummary(profile))));
            }
        }

        return MatchOutcome<ClubMatchListResponse>.Ok(new ClubMatchListResponse(TakeBest(ranked)));
    }

    public static async Task<MatchOutcome<CompanyMatchListResponse>> CompaniesForClubAsync(
        Guid accountId,
        string? query,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseQuery(query, out var parsed))
        {
            return MatchOutcome<CompanyMatchListResponse>.Fail(SponsorshipMatchErrors.QueryInvalid);
        }

        var profile = await dbContext.ClubProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return MatchOutcome<CompanyMatchListResponse>.Fail(SponsorshipMatchErrors.ClubProfileNotFound);
        }

        if (profile.Status != ClubProfileStatuses.Published)
        {
            return MatchOutcome<CompanyMatchListResponse>.Fail(SponsorshipMatchErrors.ClubProfileNotPublished);
        }

        var club = ToMatchClub(profile);
        var sets = await dbContext.SponsorshipGoalSets
            .AsNoTracking()
            .Where(s => s.Status == SponsorshipGoalStatuses.Active)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(MaxCandidates)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ranked = new List<Ranked<CompanyMatch>>();
        foreach (var set in sets)
        {
            var goal = ToMatchGoal(set);
            var assessed = Evaluate(parsed, goal, club, SearchableText(goal), MatchPerspective.Club);
            if (assessed is { } a)
            {
                ranked.Add(new Ranked<CompanyMatch>(a.Band, a.Points, new CompanyMatch(
                    a.Band.ToString(), a.Reasons, BrowseCompanyGoalsHandler.ToSummary(set))));
            }
        }

        return MatchOutcome<CompanyMatchListResponse>.Ok(new CompanyMatchListResponse(TakeBest(ranked)));
    }

    private readonly record struct Ranked<T>(FitBand Band, int Points, T Match);

    private readonly record struct Evaluation(FitBand Band, int Points, IReadOnlyList<string> Reasons);

    private static bool TryParseQuery(string? query, out ParsedMatchQuery parsed)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length > SponsorshipMatchQuery.MaxLength)
        {
            parsed = ParsedMatchQuery.Empty;
            return false;
        }

        parsed = SponsorshipMatchQuery.Parse(trimmed);
        return true;
    }

    /// <summary>
    /// Without a usable search: only real overlaps. With one: only candidates the words match, and a
    /// match with no real overlap is Partial with the search reason. Reasons put the search first.
    /// </summary>
    private static Evaluation? Evaluate(
        ParsedMatchQuery parsed,
        MatchGoal goal,
        MatchClub club,
        (string Text, IReadOnlyList<int> Years) searchable,
        MatchPerspective perspective)
    {
        var overlap = SponsorshipMatcher.ComputeOverlap(goal, club);
        var band = SponsorshipMatcher.BandOf(overlap);
        var points = SponsorshipMatcher.PointsOf(overlap);
        var reasons = SponsorshipMatcher.ReasonsFor(overlap, perspective);

        if (parsed.IsEmpty)
        {
            return band is null ? null : new Evaluation(band.Value, points, reasons);
        }

        var found = SponsorshipMatchQuery.Match(parsed, searchable.Text, searchable.Years);
        if (found is null)
        {
            return null;
        }

        var withSearch = SponsorshipMatchQuery.ReasonsFor(found).Concat(reasons).Take(SponsorshipMatcher.MaxReasons).ToList();
        return new Evaluation(band ?? FitBand.Partial, points, withSearch);
    }

    private static List<T> TakeBest<T>(List<Ranked<T>> ranked) =>
        ranked
            .OrderBy(r => r.Band)
            .ThenByDescending(r => r.Points)
            .Take(MaxResults)
            .Select(r => r.Match)
            .ToList();

    private static (string Text, IReadOnlyList<int> Years) SearchableText(MatchClub club) => (
        string.Join(
            ' ',
            new[] { club.Name, club.University, club.City ?? string.Empty }
                .Concat(club.Fields)
                .Concat(club.Events.Select(e => e.Title))
                .Concat(SponsorshipMatcher.KindsRunBy(club))),
        club.Years);

    private static (string Text, IReadOnlyList<int> Years) SearchableText(MatchGoal goal) => (
        string.Join(
            ' ',
            new[] { goal.Name, goal.CompanyName }
                .Concat(goal.Objectives)
                .Concat(goal.EventKinds)
                .Concat(goal.Fields)
                .Concat(goal.Cities)
                .Concat(goal.Universities)),
        goal.Years);

    private static MatchClub ToMatchClub(ClubProfile p) => new(
        p.Name,
        p.University,
        p.City,
        ClubProfileOptions.Deserialize<List<string>>(p.AudienceFieldsOfStudy, []),
        ClubProfileOptions.Deserialize<List<int>>(p.AudienceYears, []),
        ClubProfileOptions.Deserialize<List<ClubEvent>>(p.EventsJson, [])
            .Select(e => new MatchEvent(e.Title, e.Description))
            .ToList());

    private static MatchGoal ToMatchGoal(SponsorshipGoalSet s) => new(
        s.Name,
        s.CompanyName,
        SponsorshipGoalOptions.Deserialize<List<string>>(s.Objectives, []),
        SponsorshipGoalOptions.Deserialize<List<string>>(s.EventKinds, []),
        SponsorshipGoalOptions.Deserialize<List<string>>(s.AudienceFieldsOfStudy, []),
        SponsorshipGoalOptions.Deserialize<List<int>>(s.AudienceYears, []),
        SponsorshipGoalOptions.Deserialize<List<string>>(s.AudienceCities, []),
        SponsorshipGoalOptions.Deserialize<List<string>>(s.AudienceUniversities, []));
}
