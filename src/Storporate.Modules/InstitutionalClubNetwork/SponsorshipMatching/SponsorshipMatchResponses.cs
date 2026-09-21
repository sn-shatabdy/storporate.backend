using Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;
using Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipMatching;

/// <summary>
/// A club suggested for one of the caller's goal sets. <c>Fit</c> is "Strong", "Good" or "Partial";
/// there is deliberately no numeric score anywhere. The club is the same summary the club browse returns.
/// </summary>
public sealed record ClubMatch(string Fit, IReadOnlyList<string> Reasons, ClubSummaryResponse Club);

/// <summary>A company goal set suggested for the caller's club. Same shape rules as <see cref="ClubMatch"/>.</summary>
public sealed record CompanyMatch(string Fit, IReadOnlyList<string> Reasons, CompanyGoalSummary Company);

public sealed record ClubMatchListResponse(IReadOnlyList<ClubMatch> Items);

public sealed record CompanyMatchListResponse(IReadOnlyList<CompanyMatch> Items);

/// <summary>Result of a match call: a value, or the error code the endpoint maps to a status.</summary>
public sealed record MatchOutcome<T>(T? Value, string? ErrorCode)
    where T : class
{
    public static MatchOutcome<T> Ok(T value) => new(value, null);

    public static MatchOutcome<T> Fail(string errorCode) => new(null, errorCode);
}

public static class SponsorshipMatchErrors
{
    public const string GoalNotFound = "sponsorship_goal_not_found";
    public const string ClubProfileNotFound = "club_profile_not_found";
    public const string ClubProfileNotPublished = "club_profile_not_published";
    public const string QueryInvalid = "sponsorship_match_query_invalid";
}
