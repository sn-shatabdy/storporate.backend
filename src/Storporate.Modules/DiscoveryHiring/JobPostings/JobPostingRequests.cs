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
}

/// <summary>Body of <c>POST /api/discovery/job-postings/{id}/status</c>.</summary>
public sealed class ChangeJobPostingStatusRequest
{
    public string? Status { get; init; }
}
