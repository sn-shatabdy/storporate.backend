using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.Exceptions;
using Storporate.Modules.DiscoveryHiring.JobPostings;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.JobApplications;

/// <summary>
/// Organization-side applicant review (STOR-67). Every operation is scoped to postings owned by
/// the caller; another account's posting or application is indistinguishable from an unknown id
/// (404, never 403). Applicants are read from the immutable snapshot: this handler never reads a
/// student's private tables. Paused and Closed postings stay readable. Audit rows carry ids and
/// statuses only and are written after SaveChanges.
/// </summary>
public static class ReviewApplicationsHandler
{
    public enum Failure
    {
        None,
        PostingNotFound,
        ApplicationNotFound,
    }

    public readonly record struct Result<T>(T? Value, Failure Failure)
        where T : class
    {
        public bool IsSuccess => Failure == Failure.None;
    }

    public static async Task<Result<ApplicantListResponse>> ListAsync(
        Guid postingId,
        int page,
        int pageSize,
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await OwnsPostingAsync(postingId, accountId, dbContext, cancellationToken).ConfigureAwait(false))
        {
            return new(null, Failure.PostingNotFound);
        }

        var normalizedPage = page < 1 ? 1 : page;
        var normalizedSize = pageSize < 1
            ? ManageJobPostingsHandler.DefaultPageSize
            : Math.Min(pageSize, ManageJobPostingsHandler.MaxEmployerListItems);

        var baseQuery = dbContext.JobApplications
            .AsNoTracking()
            .Where(a => a.JobPostingId == postingId);

        var total = await baseQuery.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await baseQuery
            .OrderByDescending(a => a.CreatedAt)
            .Skip((normalizedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new(
            new ApplicantListResponse(
                rows.Select(ToApplicant).ToList(),
                normalizedPage,
                normalizedSize,
                total),
            Failure.None);
    }

    /// <summary>Submitted becomes Viewed as part of this call.</summary>
    public static async Task<Result<ApplicantResponse>> GetAsync(
        Guid postingId,
        Guid applicationId,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (application, failure) = await FindAsync(
            postingId, applicationId, accountId, dbContext, cancellationToken).ConfigureAwait(false);
        if (application is null)
        {
            return new(null, failure);
        }

        if (application.Status == JobApplicationStatuses.Submitted)
        {
            await SetStatusAsync(
                application, JobApplicationStatuses.Viewed, dbContext, auditLogWriter, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        return new(ToApplicant(application), Failure.None);
    }

    /// <summary>Shortlisted or NotSelected, from any current status. Same status is a no-op.</summary>
    public static async Task<Result<ApplicantResponse>> ChangeStatusAsync(
        Guid postingId,
        Guid applicationId,
        string newStatus,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (application, failure) = await FindAsync(
            postingId, applicationId, accountId, dbContext, cancellationToken).ConfigureAwait(false);
        if (application is null)
        {
            return new(null, failure);
        }

        if (application.Status != newStatus)
        {
            await SetStatusAsync(
                application, newStatus, dbContext, auditLogWriter, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        return new(ToApplicant(application), Failure.None);
    }

    private static async Task SetStatusAsync(
        JobApplication application,
        string status,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        application.Status = status;
        application.StatusChangedAt = now;
        application.UpdatedAt = now;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two employer tabs (or a GET-triggered auto-Viewed racing a concurrent
            // status POST) modified the same row between this caller's read and
            // write. Surface it as a typed conflict so the global handler maps it
            // to 409 application_conflict instead of an opaque 500.
            throw new ApplicationConflictException();
        }

        await auditLogWriter.WriteAsync(
            "job_application_status_changed",
            "JobApplication",
            application.Id.ToString(),
            JsonSerializer.Serialize(
                new { jobApplicationId = application.Id, status = application.Status },
                ApplyToJobHandler.JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static Task<bool> OwnsPostingAsync(
        Guid postingId,
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.JobPostings
            .AsNoTracking()
            .AnyAsync(p => p.Id == postingId && p.OwnerAccountId == accountId, cancellationToken);

    private static async Task<(JobApplication? Application, Failure Failure)> FindAsync(
        Guid postingId,
        Guid applicationId,
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await OwnsPostingAsync(postingId, accountId, dbContext, cancellationToken).ConfigureAwait(false))
        {
            return (null, Failure.PostingNotFound);
        }

        var application = await dbContext.JobApplications
            .FirstOrDefaultAsync(a => a.Id == applicationId && a.JobPostingId == postingId, cancellationToken)
            .ConfigureAwait(false);
        return application is null ? (null, Failure.ApplicationNotFound) : (application, Failure.None);
    }

    private static ApplicantResponse ToApplicant(JobApplication a)
    {
        var s = ApplyToJobHandler.ReadSnapshot(a.SnapshotJson);
        return new ApplicantResponse(
            a.Id,
            a.Status,
            a.CreatedAt,
            a.StatusChangedAt,
            s.DisplayName,
            s.Headline,
            s.University,
            s.FieldOfStudy,
            s.StudyYear,
            s.Items,
            s.Fit);
    }
}
