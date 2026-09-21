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

    /// <summary>Optional last day students can apply, evaluated in UTC at read time.</summary>
    public DateOnly? ApplicationDeadline { get; set; }

    /// <summary>How many openings the employer wants to fill (default 1).</summary>
    public int Openings { get; set; }

    /// <summary>Lower bound of the monthly pay in Bangladeshi taka.</summary>
    public int? CompensationMin { get; set; }

    /// <summary>Upper bound of the monthly pay in Bangladeshi taka.</summary>
    public int? CompensationMax { get; set; }

    /// <summary>True when the employer opts to show the pay to students.</summary>
    public bool ShowCompensation { get; set; }

    /// <summary>
    /// Lower-case, punctuation-normalized searchable text: title + company name + location +
    /// the normalized skill names joined by single spaces. Maintained on every write.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

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
