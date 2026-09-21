namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;

/// <summary>Body of <c>POST /api/sponsorship/goals</c> and <c>PUT /api/sponsorship/goals/{id}</c>.</summary>
public sealed class SaveSponsorshipGoalRequest
{
    public string? Name { get; init; }

    public string? CompanyName { get; init; }

    public List<string?>? Objectives { get; init; }

    public SponsorshipAudienceRequest? Audience { get; init; }

    public List<string?>? EventKinds { get; init; }

    public SponsorshipBudgetRequest? Budget { get; init; }

    public string? Notes { get; init; }
}

public sealed class SponsorshipAudienceRequest
{
    public List<string?>? FieldsOfStudy { get; init; }

    public List<int>? Years { get; init; }

    public List<string?>? Cities { get; init; }

    public List<string?>? Universities { get; init; }
}

public sealed class SponsorshipBudgetRequest
{
    public int? Min { get; init; }

    public int? Max { get; init; }

    public bool? VisibleToClubs { get; init; }
}

/// <summary>Body of <c>POST /api/sponsorship/goals/{id}/status</c>.</summary>
public sealed class SetSponsorshipGoalStatusRequest
{
    public string? Status { get; init; }
}
