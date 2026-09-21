namespace Storporate.Modules.DiscoveryHiring.JobPostings;

/// <summary>
/// Employer view of a posting. <see cref="IsExpired"/> is computed at read time
/// from the deadline and the current UTC date (it is NOT a stored column).
/// </summary>
public sealed record JobPostingResponse(
    Guid Id,
    string Title,
    string Kind,
    string CompanyName,
    string? Location,
    string WorkMode,
    string Description,
    IReadOnlyList<string> RequiredSkills,
    DateOnly? ApplicationDeadline,
    int Openings,
    JobPostingCompensationResponse Compensation,
    string Status,
    bool IsExpired,
    int ApplicantCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Body of <c>GET /api/discovery/job-postings</c>:
/// <c>items</c> (newest first, capped at 100) + per-status counts over all own postings.</summary>
public sealed record JobPostingListResponse(
    IReadOnlyList<JobPostingResponse> Items,
    JobPostingStatusCounts Counts);

/// <summary>Pay range as the employer sees their own posting: values + visibility flag.</summary>
public sealed record JobPostingCompensationResponse(int? Min, int? Max, bool VisibleToStudents);

/// <summary>Per-status count the employer sees on the list endpoint.</summary>
public sealed record JobPostingStatusCounts(
    int Open,
    int Paused,
    int Closed,
    int Total);

public sealed record JobFitSkillResponse(string Name, string Band);

/// <summary>Plain-language fit. Deliberately carries no score, number or percentage.</summary>
public sealed record JobFitDetailResponse(
    string Label,
    IReadOnlyList<JobFitSkillResponse> Matched,
    IReadOnlyList<string> Missing);

/// <summary>
/// Student view of a posting: <see cref="JobPostingResponse"/> plus the student's own fit
/// and application. Compensation exposes only the values when the employer opted in
/// (otherwise null).
/// </summary>
public sealed record JobFitResponse(
    Guid Id,
    string Title,
    string Kind,
    string CompanyName,
    string? Location,
    string WorkMode,
    string Description,
    IReadOnlyList<string> RequiredSkills,
    DateOnly? ApplicationDeadline,
    int Openings,
    JobFitCompensationResponse? Compensation,
    string Status,
    bool IsExpired,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    JobFitDetailResponse Fit,
    JobFitApplicationResponse? Application);

/// <summary>Pay range as the student sees a posting: only the values, no flag.</summary>
public sealed record JobFitCompensationResponse(int? Min, int? Max);

/// <summary>Body of <c>GET /api/discovery/jobs</c>: one paged slice of the fit-ordered list.</summary>
public sealed record JobFitListResponse(
    IReadOnlyList<JobFitResponse> Items,
    int Page,
    int PageSize,
    int Total);

/// <summary>The caller's own application to a posting (STOR-67).</summary>
public sealed record JobFitApplicationResponse(Guid Id, string Status);
