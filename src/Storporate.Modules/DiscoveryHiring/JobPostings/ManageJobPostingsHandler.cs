using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.Exceptions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.JobPostings;

/// <summary>
/// Organization-side job posting operations (STOR-66). Every operation is scoped to
/// <see cref="JobPosting.OwnerAccountId"/> == caller account; another account's id is
/// indistinguishable from an unknown id (404, never 403). Audit rows carry ids only, never
/// the title or description, and are written after SaveChanges.
/// </summary>
public static class ManageJobPostingsHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<JobPostingResponse> CreateAsync(
        SaveJobPostingRequest request,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var posting = new JobPosting
        {
            Id = Guid.NewGuid(),
            OwnerAccountId = accountId,
            Title = request.Title!.Trim(),
            Kind = request.Kind!,
            CompanyName = request.CompanyName!.Trim(),
            Location = NormalizeLocation(request.Location),
            WorkMode = request.WorkMode!,
            Description = request.Description!.Trim(),
            RequiredSkillsJson = JobPostingSkills.Serialize(JobPostingSkills.Normalize(request.RequiredSkills!)),
            Status = JobPostingStatuses.Open,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.JobPostings.Add(posting);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditLogWriter.WriteAsync(
            "job_posting_created",
            "JobPosting",
            posting.Id.ToString(),
            JsonSerializer.Serialize(new { jobPostingId = posting.Id }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        return ToResponse(posting);
    }

    public static async Task<JobPostingListResponse> ListAsync(
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.JobPostings
            .AsNoTracking()
            .Where(p => p.OwnerAccountId == accountId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new JobPostingListResponse(rows.Select(ToResponse).ToList());
    }

    public static async Task<JobPostingResponse?> GetAsync(
        Guid id,
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var posting = await dbContext.JobPostings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        return posting is null ? null : ToResponse(posting);
    }

    /// <summary>Returns null when not found. Throws <see cref="JobPostingClosedException"/> for a Closed posting.</summary>
    public static async Task<JobPostingResponse?> UpdateAsync(
        Guid id,
        SaveJobPostingRequest request,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var posting = await dbContext.JobPostings
            .FirstOrDefaultAsync(p => p.Id == id && p.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (posting is null)
        {
            return null;
        }

        if (posting.Status == JobPostingStatuses.Closed)
        {
            throw new JobPostingClosedException();
        }

        posting.Title = request.Title!.Trim();
        posting.Kind = request.Kind!;
        posting.CompanyName = request.CompanyName!.Trim();
        posting.Location = NormalizeLocation(request.Location);
        posting.WorkMode = request.WorkMode!;
        posting.Description = request.Description!.Trim();
        posting.RequiredSkillsJson = JobPostingSkills.Serialize(JobPostingSkills.Normalize(request.RequiredSkills!));
        posting.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditLogWriter.WriteAsync(
            "job_posting_updated",
            "JobPosting",
            posting.Id.ToString(),
            JsonSerializer.Serialize(new { jobPostingId = posting.Id }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        return ToResponse(posting);
    }

    /// <summary>
    /// Open&lt;-&gt;Paused and Open|Paused-&gt;Closed. Any change from Closed throws
    /// <see cref="JobPostingClosedException"/>; a same-status call on a non-Closed posting is a no-op.
    /// </summary>
    public static async Task<JobPostingResponse?> ChangeStatusAsync(
        Guid id,
        string newStatus,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var posting = await dbContext.JobPostings
            .FirstOrDefaultAsync(p => p.Id == id && p.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (posting is null)
        {
            return null;
        }

        if (posting.Status == JobPostingStatuses.Closed)
        {
            throw new JobPostingClosedException();
        }

        if (posting.Status == newStatus)
        {
            return ToResponse(posting);
        }

        var now = timeProvider.GetUtcNow();
        posting.Status = newStatus;
        posting.UpdatedAt = now;
        if (newStatus == JobPostingStatuses.Closed)
        {
            posting.ClosedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditLogWriter.WriteAsync(
            "job_posting_status_changed",
            "JobPosting",
            posting.Id.ToString(),
            JsonSerializer.Serialize(new { jobPostingId = posting.Id, status = posting.Status }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        return ToResponse(posting);
    }

    internal static JobPostingResponse ToResponse(JobPosting p) => new(
        p.Id,
        p.Title,
        p.Kind,
        p.CompanyName,
        p.Location,
        p.WorkMode,
        p.Description,
        JobPostingSkills.Deserialize(p.RequiredSkillsJson),
        p.Status,
        p.CreatedAt,
        p.UpdatedAt);

    private static string? NormalizeLocation(string? location)
    {
        var trimmed = location?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
