namespace Storporate.Modules.DiscoveryHiring.JobPostings;

public sealed record JobPostingResponse(
    Guid Id,
    string Title,
    string Kind,
    string CompanyName,
    string? Location,
    string WorkMode,
    string Description,
    IReadOnlyList<string> RequiredSkills,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record JobPostingListResponse(IReadOnlyList<JobPostingResponse> Items);

public sealed record JobFitSkillResponse(string Name, string Band);

/// <summary>Plain-language fit. Deliberately carries no score, number or percentage.</summary>
public sealed record JobFitDetailResponse(
    string Label,
    IReadOnlyList<JobFitSkillResponse> Matched,
    IReadOnlyList<string> Missing);

/// <summary>All <see cref="JobPostingResponse"/> fields plus the student's fit.</summary>
public sealed record JobFitResponse(
    Guid Id,
    string Title,
    string Kind,
    string CompanyName,
    string? Location,
    string WorkMode,
    string Description,
    IReadOnlyList<string> RequiredSkills,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    JobFitDetailResponse Fit);

public sealed record JobFitListResponse(IReadOnlyList<JobFitResponse> Items);
