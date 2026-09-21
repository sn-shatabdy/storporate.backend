namespace Storporate.SharedKernel.Entities;

/// <summary>
/// STOR-66: a job or internship posting published by an Organization. Deliberately NOT
/// <see cref="IAccountScoped"/> (like <see cref="TalentIndexEntry"/>): students must read
/// Open postings owned by other accounts. Owner scoping for the employer endpoints is
/// enforced in the handlers via <see cref="OwnerAccountId"/>.
/// </summary>
public sealed class JobPosting
{
    public Guid Id { get; set; }

    public Guid OwnerAccountId { get; set; }

    public User? Owner { get; set; }

    public required string Title { get; set; }

    public required string Kind { get; set; }

    public required string CompanyName { get; set; }

    public string? Location { get; set; }

    public required string WorkMode { get; set; }

    public required string Description { get; set; }

    /// <summary>JSON array of trimmed, case-insensitively de-duplicated skill names.</summary>
    public required string RequiredSkillsJson { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
}

public static class JobPostingKinds
{
    public const string Job = "Job";
    public const string Internship = "Internship";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Job,
        Internship,
    };
}

public static class JobPostingWorkModes
{
    public const string OnSite = "OnSite";
    public const string Remote = "Remote";
    public const string Hybrid = "Hybrid";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        OnSite,
        Remote,
        Hybrid,
    };
}

public static class JobPostingStatuses
{
    public const string Open = "Open";
    public const string Paused = "Paused";
    public const string Closed = "Closed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Open,
        Paused,
        Closed,
    };
}
