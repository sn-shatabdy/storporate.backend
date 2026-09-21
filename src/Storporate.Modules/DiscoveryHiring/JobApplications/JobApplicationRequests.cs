namespace Storporate.Modules.DiscoveryHiring.JobApplications;

/// <summary>Body of <c>POST /api/discovery/jobs/{jobId}/applications</c>. The body is optional.</summary>
public sealed class ApplyToJobRequest
{
    /// <summary>Only used when the student has no profile with a display name.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>Body of <c>POST /api/discovery/job-postings/{id}/applications/{applicationId}/status</c>.</summary>
public sealed class ChangeApplicationStatusRequest
{
    public string? Status { get; init; }
}
