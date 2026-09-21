namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;

public sealed record SponsorshipAudienceResponse(
    IReadOnlyList<string> FieldsOfStudy,
    IReadOnlyList<int> Years,
    IReadOnlyList<string> Cities,
    IReadOnlyList<string> Universities);

/// <summary>Budget as the owning company sees it (amounts in BDT).</summary>
public sealed record SponsorshipOwnBudgetResponse(int? Min, int? Max, bool VisibleToClubs);

/// <summary>Budget as a club sees it: only present when the company chose to show it.</summary>
public sealed record SponsorshipPublicBudgetResponse(int? Min, int? Max);

/// <summary>The company's own goal set. Never carries the owner account id, email or any account data.</summary>
public sealed record SponsorshipGoalSetResponse(
    Guid Id,
    string Name,
    string CompanyName,
    IReadOnlyList<string> Objectives,
    SponsorshipAudienceResponse Audience,
    IReadOnlyList<string> EventKinds,
    SponsorshipOwnBudgetResponse Budget,
    string? Notes,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SponsorshipGoalSetListResponse(IReadOnlyList<SponsorshipGoalSetResponse> Items);

public sealed record CompanyGoalSummary(
    Guid Id,
    string Name,
    string CompanyName,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> EventKinds,
    IReadOnlyList<string> FieldsOfStudy,
    SponsorshipPublicBudgetResponse? Budget);

public sealed record CompanyGoalListResponse(IReadOnlyList<CompanyGoalSummary> Items);

/// <summary>Club-facing detail of an Active goal set. Never carries the owner account id, email or any account data.</summary>
public sealed record CompanyGoalDetail(
    Guid Id,
    string Name,
    string CompanyName,
    IReadOnlyList<string> Objectives,
    SponsorshipAudienceResponse Audience,
    IReadOnlyList<string> EventKinds,
    SponsorshipPublicBudgetResponse? Budget,
    string? Notes,
    DateTimeOffset UpdatedAt);
