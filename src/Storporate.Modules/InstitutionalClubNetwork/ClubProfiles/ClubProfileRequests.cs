namespace Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;

/// <summary>Body of <c>PUT /api/clubs/profile</c>.</summary>
public sealed class SaveClubProfileRequest
{
    public string? Name { get; init; }

    public string? Tagline { get; init; }

    public string? About { get; init; }

    public string? University { get; init; }

    public string? City { get; init; }

    public int? FoundedYear { get; init; }

    public int? MemberCount { get; init; }

    public ClubAudienceRequest? Audience { get; init; }

    public List<ClubEventRequest>? Events { get; init; }
}

public sealed class ClubAudienceRequest
{
    public List<string?>? FieldsOfStudy { get; init; }

    public List<int>? Years { get; init; }
}

public sealed class ClubEventRequest
{
    public Guid? Id { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public int? TypicalAttendance { get; init; }

    public string? Frequency { get; init; }

    public List<string?>? SupportNeeds { get; init; }
}
