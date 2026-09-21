namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipRequests;

/// <summary>
/// One row of a request list. <c>CounterpartName</c> is the company name on the club side and the
/// club name on the company side. No owner account id or email is ever part of a response.
/// </summary>
public sealed record SponsorshipRequestSummary(
    Guid Id,
    string Status,
    string EventTitle,
    DateOnly? EventDate,
    string CounterpartName,
    string GoalName,
    int? AmountRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount);

public sealed record SponsorshipRequestListResponse(IReadOnlyList<SponsorshipRequestSummary> Items);

public sealed record SponsorshipRequestClubInfo(string Name, string University);

public sealed record SponsorshipRequestCompanyInfo(string Name, string GoalName);

public sealed record SponsorshipRequestOutcomeInfo(string Note, int? AgreedAmount);

public sealed record SponsorshipRequestMessageResponse(Guid Id, string From, string Body, DateTimeOffset CreatedAt);

public sealed record SponsorshipRequestDetail(
    Guid Id,
    string Status,
    string EventTitle,
    DateOnly? EventDate,
    string EventDescription,
    string Ask,
    int? AmountRequested,
    string Offer,
    SponsorshipRequestClubInfo Club,
    SponsorshipRequestCompanyInfo Company,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ViewedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? CompletedAt,
    string? DecisionNote,
    SponsorshipRequestOutcomeInfo? Outcome,
    IReadOnlyList<SponsorshipRequestMessageResponse> Messages,
    IReadOnlyList<string> AllowedActions);

/// <summary>Result of a request call: a value, or the error code the endpoint maps to a status.</summary>
public sealed record SponsorshipRequestOutcome<T>(T? Value, string? ErrorCode)
    where T : class
{
    public static SponsorshipRequestOutcome<T> Ok(T value) => new(value, null);

    public static SponsorshipRequestOutcome<T> Fail(string errorCode) => new(null, errorCode);
}

public static class SponsorshipRequestErrors
{
    public const string NotFound = "sponsorship_request_not_found";
    public const string Duplicate = "sponsorship_request_duplicate";
    public const string Closed = "sponsorship_request_closed";
    public const string ThreadFull = "sponsorship_request_thread_full";
    public const string InvalidTransition = "sponsorship_request_invalid_transition";
    public const string StatusInvalid = "sponsorship_request_status_invalid";
    public const string ClubProfileNotFound = "club_profile_not_found";
    public const string ClubProfileNotPublished = "club_profile_not_published";
    public const string GoalNotFound = "sponsorship_goal_not_found";
}
