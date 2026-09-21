namespace Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;

/// <summary>One event of a club. Also the stored JSON shape of <c>ClubProfile.EventsJson</c>.</summary>
public sealed record ClubEvent(
    Guid Id,
    string Title,
    string? Description,
    int TypicalAttendance,
    string Frequency,
    IReadOnlyList<string> SupportNeeds);

public sealed record ClubAudienceResponse(IReadOnlyList<string> FieldsOfStudy, IReadOnlyList<int> Years);

/// <summary>Full club profile. Never carries the owner account id, email or any account data.</summary>
public sealed record ClubProfileResponse(
    Guid Id,
    string Name,
    string? Tagline,
    string About,
    string University,
    string? City,
    int? FoundedYear,
    int MemberCount,
    ClubAudienceResponse Audience,
    IReadOnlyList<ClubEvent> Events,
    string Status,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt);

public sealed record ClubSummaryResponse(
    Guid Id,
    string Name,
    string? Tagline,
    string University,
    int MemberCount,
    IReadOnlyList<string> FieldsOfStudy,
    int EventCount);

public sealed record ClubListResponse(IReadOnlyList<ClubSummaryResponse> Items);
