using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.Exceptions;
using Storporate.Modules.DiscoveryHiring.JobPostings;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.JobApplications;

/// <summary>
/// Student-side application operations (STOR-67). The applicant snapshot is built in the
/// student's own request (the student can read their own rows) so the employer never has to
/// touch the student's private tables. The snapshot carries label, category and skill bands
/// only: never descriptions, file names, storage keys, URLs, reasons or originals.
/// </summary>
public static class ApplyToJobHandler
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns null when the posting is not Open (or unknown).</summary>
    public static async Task<ApplicationResponse?> ApplyAsync(
        Guid jobId,
        ApplyToJobRequest request,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var posting = await dbContext.JobPostings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == jobId && p.Status == JobPostingStatuses.Open, cancellationToken)
            .ConfigureAwait(false);
        if (posting is null)
        {
            return null;
        }

        if (await ExistsAsync(dbContext, jobId, accountId, cancellationToken).ConfigureAwait(false))
        {
            throw new ApplicationAlreadySubmittedException();
        }

        var snapshot = await BuildSnapshotAsync(posting, request, dbContext, cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var application = new JobApplication
        {
            Id = Guid.NewGuid(),
            JobPostingId = posting.Id,
            StudentAccountId = accountId,
            Status = JobApplicationStatuses.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
            StatusChangedAt = now,
            SnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions),
        };
        dbContext.JobApplications.Add(application);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(application).State = EntityState.Detached;
            if (await ExistsAsync(dbContext, jobId, accountId, cancellationToken).ConfigureAwait(false))
            {
                // Lost a race with a concurrent apply: the unique (posting, student) index rejected us.
                throw new ApplicationAlreadySubmittedException();
            }

            throw;
        }

        await auditLogWriter.WriteAsync(
            "job_application_submitted",
            "JobApplication",
            application.Id.ToString(),
            JsonSerializer.Serialize(
                new { jobApplicationId = application.Id, jobPostingId = posting.Id }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        return ToStudentResponse(application, posting.Title, posting.CompanyName, posting.Kind);
    }

    public static async Task<ApplicationListResponse> ListOwnAsync(
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.JobApplications
            .AsNoTracking()
            .Where(a => a.StudentAccountId == accountId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                Application = a,
                a.JobPosting!.Title,
                a.JobPosting.CompanyName,
                a.JobPosting.Kind,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ApplicationListResponse(rows
            .Select(r => ToStudentResponse(r.Application, r.Title, r.CompanyName, r.Kind))
            .ToList());
    }

    private static Task<bool> ExistsAsync(
        WriteDbContext dbContext,
        Guid jobId,
        Guid accountId,
        CancellationToken cancellationToken) =>
        dbContext.JobApplications
            .AsNoTracking()
            .AnyAsync(a => a.JobPostingId == jobId && a.StudentAccountId == accountId, cancellationToken);

    private static async Task<ApplicantSnapshot> BuildSnapshotAsync(
        JobPosting posting,
        ApplyToJobRequest request,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // Every read below is account-scoped by the global query filter (and RLS): own rows only.
        var profile = await dbContext.StudentSearchProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var displayName = !string.IsNullOrWhiteSpace(profile?.DisplayName)
            ? profile!.DisplayName.Trim()
            : request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure(nameof(ApplyToJobRequest.DisplayName), "Tell the employer your name to apply.")
                {
                    ErrorCode = "application_display_name_required",
                },
            });
        }

        var items = await LoadItemsAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var studentSkills = await BrowseJobsHandler.LoadStudentSkillsAsync(dbContext, cancellationToken)
            .ConfigureAwait(false);
        var fit = BrowseJobsHandler.ComputeFit(posting, studentSkills);

        return new ApplicantSnapshot(
            displayName,
            profile is { ShowHeadline: true } ? profile.Headline : null,
            profile is { ShowUniversity: true } ? profile.University : null,
            profile is { ShowFieldOfStudy: true } ? profile.FieldOfStudy : null,
            profile is { ShowStudyYear: true } ? profile.StudyYear : null,
            items,
            fit);
    }

    /// <summary>Analyzed items with at least one Strong or Developing skill: label, category, skill bands.</summary>
    private static async Task<List<ApplicantItemResponse>> LoadItemsAsync(
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var items = await dbContext.PortfolioItems
            .AsNoTracking()
            .Where(i => i.AnalysisStatus == PortfolioAnalysisStatuses.Analyzed)
            .OrderBy(i => i.CreatedAt)
            .Select(i => new { i.Id, i.Label, i.Category, i.CustomCategoryText })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (items.Count == 0)
        {
            return [];
        }

        var itemIds = items.Select(i => i.Id).ToList();
        var findings = await dbContext.PortfolioSkillFindings
            .AsNoTracking()
            .Where(f => itemIds.Contains(f.PortfolioItemId)
                && (f.ConfidenceBand == ConfidenceBands.Strong || f.ConfidenceBand == ConfidenceBands.Developing))
            .OrderBy(f => f.CreatedAt)
            .Select(f => new { f.PortfolioItemId, f.SkillName, f.ConfidenceBand })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byItem = findings.ToLookup(f => f.PortfolioItemId);

        var result = new List<ApplicantItemResponse>();
        foreach (var item in items)
        {
            var skills = byItem[item.Id]
                .Select(f => new JobFitSkillResponse(f.SkillName, f.ConfidenceBand))
                .ToList();
            if (skills.Count == 0)
            {
                continue;
            }

            var category = item.Category == PortfolioCategories.Other
                ? item.CustomCategoryText ?? "Other"
                : item.Category;
            result.Add(new ApplicantItemResponse(item.Label, category, skills));
        }

        return result;
    }

    internal static ApplicationResponse ToStudentResponse(
        JobApplication application,
        string jobTitle,
        string companyName,
        string kind)
    {
        var snapshot = ReadSnapshot(application.SnapshotJson);
        return new ApplicationResponse(
            application.Id,
            application.JobPostingId,
            jobTitle,
            companyName,
            kind,
            application.Status,
            application.CreatedAt,
            application.StatusChangedAt,
            snapshot.Fit.Label);
    }

    internal static ApplicantSnapshot ReadSnapshot(string json) =>
        JsonSerializer.Deserialize<ApplicantSnapshot>(json, JsonOptions)!;
}
