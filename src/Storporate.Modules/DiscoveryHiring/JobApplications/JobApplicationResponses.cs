using Storporate.Modules.DiscoveryHiring.JobPostings;

namespace Storporate.Modules.DiscoveryHiring.JobApplications;

/// <summary>Student view of one of their own applications.</summary>
public sealed record ApplicationResponse(
    Guid Id,
    Guid JobPostingId,
    string JobTitle,
    string CompanyName,
    string Kind,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset StatusChangedAt,
    string FitLabel);

public sealed record ApplicationListResponse(IReadOnlyList<ApplicationResponse> Items);

/// <summary>One shared item in an applicant snapshot: label, category and skill bands only.</summary>
public sealed record ApplicantItemResponse(
    string Label,
    string Category,
    IReadOnlyList<JobFitSkillResponse> Skills);

/// <summary>Employer view of one applicant, read from the immutable snapshot.</summary>
public sealed record ApplicantResponse(
    Guid Id,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset StatusChangedAt,
    string DisplayName,
    string? Headline,
    string? University,
    string? FieldOfStudy,
    int? StudyYear,
    IReadOnlyList<ApplicantItemResponse> Items,
    JobFitDetailResponse Fit);

public sealed record ApplicantListResponse(IReadOnlyList<ApplicantResponse> Items);

/// <summary>The stored snapshot JSON shape (captured at apply time, never updated).</summary>
internal sealed record ApplicantSnapshot(
    string DisplayName,
    string? Headline,
    string? University,
    string? FieldOfStudy,
    int? StudyYear,
    IReadOnlyList<ApplicantItemResponse> Items,
    JobFitDetailResponse Fit);
