namespace Storporate.Modules.DiscoveryHiring.JobPostings;

/// <summary>Body of <c>POST</c> and <c>PUT /api/discovery/job-postings</c>.</summary>
public sealed class SaveJobPostingRequest
{
    public string? Title { get; init; }

    public string? Kind { get; init; }

    public string? CompanyName { get; init; }

    public string? Location { get; init; }

    public string? WorkMode { get; init; }

    public string? Description { get; init; }

    public List<string>? RequiredSkills { get; init; }

    /// <summary>Optional UTC date-only. Omitted (or null) means no deadline.</summary>
    public DateOnly? ApplicationDeadline { get; init; }

    /// <summary>Optional. Omitted keeps the existing value on PUT (default 1 on POST).</summary>
    public int? Openings { get; init; }

    /// <summary>Optional pay range in Bangladeshi taka per month. Omitted keeps existing values.</summary>
    public JobPostingCompensationRequest? Compensation { get; init; }
}

/// <summary>Body of <c>POST /api/discovery/job-postings/{id}/status</c>.</summary>
public sealed class ChangeJobPostingStatusRequest
{
    public string? Status { get; init; }
}

/// <summary>Pay range in Bangladeshi taka per month. Either bound may be omitted.
/// <see cref="VisibleToStudents"/> is the employer's "Show pay to students" switch.</summary>
public sealed class JobPostingCompensationRequest
{
    public int? Min { get; init; }

    public int? Max { get; init; }

    public bool VisibleToStudents { get; init; }
}
