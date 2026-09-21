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

    /// <summary>Maximum items returned by the employer list endpoint.</summary>
    public const int MaxEmployerListItems = 100;

    /// <summary>Default page size for the paged surfaces that follow the
    /// STOR-66 fit-list convention (used by the student browse and the STOR-67
    /// application lists — my-applications + employer applicant list).</summary>
    public const int DefaultPageSize = 20;

    /// <summary>Maximum page size callers may request via <c>pageSize</c>; values
    /// above this clamp down to the cap.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Maximum items the student browse computes fit against before sorting + paging.</summary>
    public const int MaxFitCandidates = 300;

    public static async Task<JobPostingResponse> CreateAsync(
        SaveJobPostingRequest request,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var normalizedSkills = JobPostingSkills.Normalize(request.RequiredSkills!);
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
            RequiredSkillsJson = JobPostingSkills.Serialize(normalizedSkills),
            Status = JobPostingStatuses.Open,
            ApplicationDeadline = request.ApplicationDeadline,
            Openings = request.Openings ?? 1,
            CompensationMin = request.Compensation?.Min,
            CompensationMax = request.Compensation?.Max,
            ShowCompensation = request.Compensation?.VisibleToStudents ?? false,
            SearchText = JobPostingSkills.BuildSearchText(
                request.Title.Trim(), request.CompanyName.Trim(),
                NormalizeLocation(request.Location), normalizedSkills),
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

        return ToResponse(posting, applicantCount: 0);
    }

    public static async Task<JobPostingListResponse> ListAsync(
        string? status,
        string? query,
        Guid accountId,
        WriteDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeQuery(query);

        var allOwn = dbContext.JobPostings
            .AsNoTracking()
            .Where(p => p.OwnerAccountId == accountId);

        // counts ignore status / q filters — always over every own posting.
        var grouped = await allOwn
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var counts = new JobPostingStatusCounts(
            Open: grouped.FirstOrDefault(g => g.Status == JobPostingStatuses.Open)?.Count ?? 0,
            Paused: grouped.FirstOrDefault(g => g.Status == JobPostingStatuses.Paused)?.Count ?? 0,
            Closed: grouped.FirstOrDefault(g => g.Status == JobPostingStatuses.Closed)?.Count ?? 0,
            Total: grouped.Sum(g => g.Count));

        var filtered = allOwn;
        if (!string.IsNullOrEmpty(status))
        {
            filtered = filtered.Where(p => p.Status == status);
        }

        if (normalizedQuery is not null)
        {
            var padded = $" {normalizedQuery} ";
            filtered = filtered.Where(p => (" " + p.SearchText + " ").Contains(padded));
        }

        var rows = await filtered
            .OrderByDescending(p => p.CreatedAt)
            .Take(MaxEmployerListItems)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var applicantCounts = await LoadApplicantCountsAsync(
            dbContext, rows.Select(p => p.Id), cancellationToken).ConfigureAwait(false);

        return new JobPostingListResponse(
            rows.Select(p => ToResponse(p, applicantCounts.GetValueOrDefault(p.Id))).ToList(),
            counts);
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
        if (posting is null)
        {
            return null;
        }

        var applicantCount = await LoadApplicantCountsAsync(
            dbContext, new[] { posting.Id }, cancellationToken).ConfigureAwait(false);
        return ToResponse(posting, applicantCount: applicantCount.GetValueOrDefault(posting.Id));
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

        var normalizedSkills = JobPostingSkills.Normalize(request.RequiredSkills!);
        posting.Title = request.Title!.Trim();
        posting.Kind = request.Kind!;
        posting.CompanyName = request.CompanyName!.Trim();
        posting.Location = NormalizeLocation(request.Location);
        posting.WorkMode = request.WorkMode!;
        posting.Description = request.Description!.Trim();
        posting.RequiredSkillsJson = JobPostingSkills.Serialize(normalizedSkills);
        posting.ApplicationDeadline = request.ApplicationDeadline;
        posting.Openings = request.Openings ?? 1;
        posting.CompensationMin = request.Compensation?.Min ?? posting.CompensationMin;
        posting.CompensationMax = request.Compensation?.Max ?? posting.CompensationMax;
        posting.ShowCompensation = request.Compensation?.VisibleToStudents ?? posting.ShowCompensation;
        posting.SearchText = JobPostingSkills.BuildSearchText(
            posting.Title, posting.CompanyName, posting.Location, normalizedSkills);
        posting.UpdatedAt = timeProvider.GetUtcNow();
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new JobPostingConflictException();
        }

        await auditLogWriter.WriteAsync(
            "job_posting_updated",
            "JobPosting",
            posting.Id.ToString(),
            JsonSerializer.Serialize(new { jobPostingId = posting.Id }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        var applicantCount = await LoadApplicantCountsAsync(
            dbContext, new[] { posting.Id }, cancellationToken).ConfigureAwait(false);
        return ToResponse(posting, applicantCount: applicantCount.GetValueOrDefault(posting.Id));
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
            var applicantCount = await LoadApplicantCountsAsync(
                dbContext, new[] { posting.Id }, cancellationToken).ConfigureAwait(false);
            return ToResponse(posting, applicantCount: applicantCount.GetValueOrDefault(posting.Id));
        }

        var now = timeProvider.GetUtcNow();
        posting.Status = newStatus;
        posting.UpdatedAt = now;
        if (newStatus == JobPostingStatuses.Closed)
        {
            posting.ClosedAt = now;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new JobPostingConflictException();
        }

        await auditLogWriter.WriteAsync(
            "job_posting_status_changed",
            "JobPosting",
            posting.Id.ToString(),
            JsonSerializer.Serialize(new { jobPostingId = posting.Id, status = posting.Status }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        var statusChangedApplicantCount = await LoadApplicantCountsAsync(
            dbContext, new[] { posting.Id }, cancellationToken).ConfigureAwait(false);
        return ToResponse(
            posting,
            applicantCount: statusChangedApplicantCount.GetValueOrDefault(posting.Id));
    }

    internal static JobPostingResponse ToResponse(JobPosting p, int applicantCount) => new(
        p.Id,
        p.Title,
        p.Kind,
        p.CompanyName,
        p.Location,
        p.WorkMode,
        p.Description,
        JobPostingSkills.Deserialize(p.RequiredSkillsJson),
        p.ApplicationDeadline,
        p.Openings,
        new JobPostingCompensationResponse(p.CompensationMin, p.CompensationMax, p.ShowCompensation),
        p.Status,
        IsExpiredAt(p, DateOnly.FromDateTime(DateTime.UtcNow)),
        applicantCount,
        p.CreatedAt,
        p.UpdatedAt);

    /// <summary>A posting is expired when it's Open and its deadline (UTC date) is before today.</summary>
    internal static bool IsExpiredAt(JobPosting p, DateOnly todayUtc) =>
        p.Status == JobPostingStatuses.Open
            && p.ApplicationDeadline.HasValue
            && p.ApplicationDeadline.Value < todayUtc;

    private static string? NormalizeLocation(string? location)
    {
        var trimmed = location?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static async Task<Dictionary<Guid, int>> LoadApplicantCountsAsync(
        WriteDbContext dbContext,
        IEnumerable<Guid> postingIds,
        CancellationToken cancellationToken)
    {
        var ids = postingIds.ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var rows = await dbContext.JobApplications
            .AsNoTracking()
            .Where(a => ids.Contains(a.JobPostingId))
            .GroupBy(a => a.JobPostingId)
            .Select(g => new { JobPostingId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.JobPostingId, r => r.Count);
    }

    /// <summary>Lower-case + collapse whitespace + 100-char cap; null when query is empty.</summary>
    internal static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmed = query.Trim();
        if (trimmed.Length > 100)
        {
            throw new JobPostingQueryInvalidException();
        }

        return trimmed.ToLowerInvariant();
    }
}
