namespace Storporate.SharedKernel.Entities;

/// <summary>
/// STOR-69: the structured public profile of a Club account, readable by companies once
/// Published. Deliberately NOT <see cref="IAccountScoped"/> (like <see cref="JobPosting"/>):
/// Organizations must read Published profiles owned by other accounts. Owner scoping for the
/// club endpoints is enforced in the handlers via <see cref="OwnerAccountId"/>, which is unique
/// (one profile per club account) and is never exposed in any response.
/// </summary>
public sealed class ClubProfile
{
    public Guid Id { get; set; }

    public Guid OwnerAccountId { get; set; }

    public User? Owner { get; set; }

    public required string Name { get; set; }

    public string? Tagline { get; set; }

    public required string About { get; set; }

    public required string University { get; set; }

    public string? City { get; set; }

    public int? FoundedYear { get; set; }

    public int MemberCount { get; set; }

    /// <summary>JSON array of trimmed, case-insensitively de-duplicated field-of-study names.</summary>
    public required string AudienceFieldsOfStudy { get; set; }

    /// <summary>JSON array of study years (1..6), de-duplicated and sorted.</summary>
    public required string AudienceYears { get; set; }

    /// <summary>JSON array of the club's events (id, title, description, typical attendance, frequency, support needs).</summary>
    public required string EventsJson { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}

public static class ClubProfileStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Draft,
        Published,
    };
}
