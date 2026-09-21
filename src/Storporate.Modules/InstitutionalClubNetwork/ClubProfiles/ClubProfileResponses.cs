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

/// <summary>Aggregate min/max typical attendance across a club's events. Both null when no event carries a value.</summary>
public sealed record ClubEventAttendanceSummary(int? Min, int? Max);

/// <summary>Company-side browse summary (STOR-69). Enriched in the redo to support an
/// "audience snapshot" glance: founded year, audience study years, event attendance range,
/// and the union of all events' support needs. Existing fields kept intact for STOR-71
/// matcher compatibility (the match-card surface reads the same <c>name</c>, <c>university</c>,
/// etc.).</summary>
public sealed record ClubSummaryResponse(
    Guid Id,
    string Name,
    string? Tagline,
    string University,
    int MemberCount,
    IReadOnlyList<string> FieldsOfStudy,
    int EventCount,
    int? FoundedYear,
    IReadOnlyList<int> AudienceYears,
    ClubEventAttendanceSummary EventAttendanceSummary,
    IReadOnlyList<string> SupportNeeds);

/// <summary>
/// Browse response. <see cref="Total"/> is the count of all matching rows before the
/// <c>Take(MaxResults)</c> truncation, so the client can show a "showing N of total" affordance
/// when the result was truncated.
/// </summary>
public sealed record ClubListResponse(IReadOnlyList<ClubSummaryResponse> Items, int Total);
