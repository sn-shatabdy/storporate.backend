using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.CandidateReview;
using Storporate.Modules.DiscoveryHiring.JobApplications;
using Storporate.Modules.DiscoveryHiring.JobPostings;
using Storporate.Modules.DiscoveryHiring.Outreach;
using Storporate.Modules.DiscoveryHiring.SearchableProfile;
using Storporate.Modules.DiscoveryHiring.TalentSearch;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Authorization;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Storage;

namespace Storporate.Modules.DiscoveryHiring;

/// <summary>
/// Minimal-API endpoints for the DiscoveryHiring module. Phase 1 ships the
/// student-facing "let employers find me" toggle surface
/// (<c>GET</c> + <c>PUT /api/discovery/searchable-profile</c>); STOR-43
/// Phase 2 adds the employer-side plain-language talent-search endpoints
/// on the same route group; STOR-44 Phase 2 adds the candidate drill-down
/// endpoints (<c>GET /api/discovery/candidates/{id}</c> and
/// <c>GET /api/discovery/candidates/{id}/items/{portfolioItemId}/original</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Permission gates.</b> Each endpoint calls
/// <c>.RequirePermission(Permissions.&lt;Area&gt;.&lt;Verb&gt;)</c>. The
/// searchable-profile endpoints use <see cref="Permissions.SearchableProfile"/>;
/// the search endpoints use <see cref="Permissions.TalentSearch"/> (Create
/// for POST, Read for GET); the drill-down endpoints share one
/// <see cref="Permissions.CandidateReview.Read"/> gate. The constants are
/// recognized by the <see cref="RequirePermissionAttribute"/> fail-fast
/// check because <see cref="Permissions.All"/> is rebuilt via reflection.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> All handlers rely on the EF Core global query
/// filter on <see cref="SharedKernel.Entities.IAccountScoped"/> so the
/// reads are scoped to the ambient account automatically. A cross-account
/// GET returns <c>404 talent_search_not_found</c> rather than <c>403</c>
/// — the row is invisible to the caller under the filter, indistinguishable
/// from a never-existed id. The drill-down endpoints read the non-tenant
/// <see cref="SharedKernel.Entities.TalentIndexEntry"/> (which has no
/// IAccountScoped filter by design) so a non-existent / never-opted-in
/// <c>candidateId</c> collapses onto <c>404 candidate_not_found</c>.
/// </para>
/// <para>
/// <b>Async shape.</b> <c>POST</c> returns <c>202 Accepted</c> with the
/// new <c>searchId</c>; the FE polls <c>GET</c> until <c>status</c> flips
/// from <c>Pending</c> to <c>Completed</c> or <c>Failed</c>. The job runs
/// through <c>SearchTalentJobProcessor</c> via the
/// <see cref="Storporate.Infrastructure.Jobs.IBackgroundJobProcessor"/>
/// dispatch list (registered in <see cref="DependencyInjection"/>).
/// </para>
/// </remarks>
public static class DiscoveryHiringEndpoints
{
    public static void MapDiscoveryHiringEndpoints(this WebApplication app)
    {
        // GET /api/discovery/searchable-profile — read the student's
        // opt-in profile + the live visibleItemCount.
        app.MapGet("/api/discovery/searchable-profile", async (
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                var accountId = accountContext.AccountId ?? accountContext.UserId!.Value;

                var response = await GetSearchableProfileHandler.ExecuteAsync(
                    dbContext,
                    accountId,
                    cancellationToken).ConfigureAwait(false);

                return Results.Ok(response);
            })
            .RequirePermission(Permissions.SearchableProfile.Read);

        // PUT /api/discovery/searchable-profile — upsert the profile, delete
        // the mirror TalentIndexEntry on opt-out, enqueue a refresh job on
        // opt-in. All in one SaveChanges where the rows are co-located.
        app.MapPut("/api/discovery/searchable-profile", async (
                UpdateSearchableProfileRequest request,
                IValidator<UpdateSearchableProfileRequest> validator,
                WriteDbContext dbContext,
                ITalentIndexRepository talentIndexRepository,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);

                var accountId = accountContext.AccountId ?? accountContext.UserId!.Value;

                var response = await UpdateSearchableProfileHandler.ExecuteAsync(
                    request,
                    dbContext,
                    talentIndexRepository,
                    auditLogWriter,
                    accountId,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);

                return Results.Ok(response);
            })
            .RequirePermission(Permissions.SearchableProfile.Update);

        // POST /api/discovery/talent-searches — start a new plain-language
        // employer search. Writes the request + the job in one SaveChanges
        // and returns 202 with the new searchId; the FE polls GET for
        // status. TalentSearchBusyException → 409 talent_search_busy (via
        // the global handler); a validation failure → 400 with one of the
        // three validator error codes.
        app.MapPost("/api/discovery/talent-searches", async (
                CreateTalentSearchRequest request,
                IValidator<CreateTalentSearchRequest> validator,
                WriteDbContext dbContext,
                IAccountContext accountContext,
                IAuditLogWriter auditLogWriter,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);

                var outcome = await CreateTalentSearchHandler.ExecuteAsync(
                    request,
                    dbContext,
                    accountContext,
                    auditLogWriter,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);

                var body = new TalentSearchAcceptedResponse(outcome.SearchId);
                return Results.Accepted(
                    uri: $"/api/discovery/talent-searches/{outcome.SearchId}",
                    value: body);
            })
            .RequirePermission(Permissions.TalentSearch.Create);

        // GET /api/discovery/talent-searches/{id} — poll a previously
        // submitted search. Tenant-scoped via the global query filter;
        // cross-account ids return 404 (mapped from
        // GetOutcome.NotFound by the endpoint). On Completed the handler
        // re-validates the stored results against the live
        // TalentIndexEntry snapshot — see GetTalentSearchHandler remarks.
        app.MapGet("/api/discovery/talent-searches/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var outcome = await GetTalentSearchHandler.ExecuteAsync(
                    id,
                    dbContext,
                    cancellationToken).ConfigureAwait(false);

                if (outcome.NotFound)
                {
                    return Results.NotFound();
                }

                return Results.Ok(outcome.Response);
            })
            .RequirePermission(Permissions.TalentSearch.Read);

        // GET /api/discovery/candidates/{candidateId} — STOR-44 Phase 2
        // drill-down. Reads ONE TalentIndexEntry (the non-tenant table an
        // Organization can read; rows live behind RLS for student-private
        // tables this endpoint will not touch). Returns 404
        // candidate_not_found when no entry matches (student opted out or
        // never opted in). The handler writes one candidate_reviewed
        // audit row per success — see GetCandidateHandler remarks.
        app.MapGet("/api/discovery/candidates/{candidateId:guid}", async (
                Guid candidateId,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                CancellationToken cancellationToken) =>
            {
                var outcome = await GetCandidateHandler.ExecuteAsync(
                    candidateId,
                    dbContext,
                    auditLogWriter,
                    cancellationToken).ConfigureAwait(false);

                if (outcome.NotFound)
                {
                    return Results.NotFound(BuildErrorBody(
                        "candidate_not_found",
                        "The candidate was not found."));
                }

                return Results.Ok(outcome.Response);
            })
            .RequirePermission(Permissions.CandidateReview.Read);

        // GET /api/discovery/candidates/{candidateId}/items/{portfolioItemId}/original
        // — STOR-44 Phase 2 drill-down surface for the original behind a
        // single shared item. Two success shapes (streaming file with
        // hardened headers, OR a small JSON url body for a Link
        // submission) and three 404 codes:
        //   * candidate_not_found      — no entry with that candidateId;
        //   * original_not_shared       — entry + item exist but the item
        //                                 has no Original descriptor (the
        //                                 student kept it private, or the
        //                                 descriptor was cleared after the
        //                                 FE cached the page);
        //   * original_unavailable      — descriptor is present but the
        //                                 underlying blob / URL cannot be
        //                                 served (missing blob in
        //                                 IArtifactStore, or a stored URL
        //                                 that does not parse as http/https).
        // Audit (one candidate_original_opened row, success only) is
        // written from the handler AFTER the blob has been located / the
        // Link has resolved and BEFORE the response body starts
        // streaming — see GetCandidateOriginalHandler remarks.
        app.MapGet(
            "/api/discovery/candidates/{candidateId:guid}/items/{portfolioItemId:guid}/original",
            async (
                Guid candidateId,
                Guid portfolioItemId,
                WriteDbContext dbContext,
                IArtifactStore artifactStore,
                IAuditLogWriter auditLogWriter,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                var outcome = await GetCandidateOriginalHandler.ExecuteAsync(
                    candidateId,
                    portfolioItemId,
                    dbContext,
                    artifactStore,
                    auditLogWriter,
                    cancellationToken).ConfigureAwait(false);

                if (outcome.NotFound)
                {
                    var code = outcome.NotFoundErrorCode ?? "candidate_not_found";
                    var message = string.Equals(code, "original_not_shared", StringComparison.Ordinal)
                        ? "The original has not been shared with employers."
                        : "The candidate was not found.";
                    return Results.NotFound(BuildErrorBody(code, message));
                }

                if (outcome.Unavailable)
                {
                    return Results.NotFound(BuildErrorBody(
                        "original_unavailable",
                        "The original is currently unavailable."));
                }

                if (outcome.LinkUrl is not null)
                {
                    // Link JSON response: just the stored URL. The
                    // security headers (nosniff, sandbox, no-store,
                    // no-referrer) do not apply to JSON metadata.
                    return Results.Ok(new { url = outcome.LinkUrl });
                }

                if (outcome.FileResponse is not null)
                {
                    var file = outcome.FileResponse;
                    httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    httpContext.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
                    httpContext.Response.Headers["Cache-Control"] = "private, no-store";
                    httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";

                    var dispositionKind = GetCandidateOriginalHandler.IsSafeInline(file.ContentType)
                        ? "inline"
                        : "attachment";
                    var disposition = BuildContentDisposition(dispositionKind, file.FileName);

                    httpContext.Response.Headers["Content-Disposition"] = disposition;

                    return Results.Stream(
                        stream: file.Content,
                        contentType: file.ContentType);
                }

                // Defensive: every code path above returns. This line is
                // unreachable but keeps the compiler honest.
                return Results.NotFound(BuildErrorBody(
                    "candidate_not_found",
                    "The candidate was not found."));
            })
            .RequirePermission(Permissions.CandidateReview.Read);

        MapJobPostingEndpoints(app);
        MapJobApplicationEndpoints(app);
        app.MapOutreachEndpoints();
    }

    /// <summary>STOR-66: Organization posting management (<c>job-postings:manage</c>) and the
    /// Student browse surface (<c>job-postings:read</c>).</summary>
    private static void MapJobPostingEndpoints(WebApplication app)
    {
        static Guid AccountOf(IAccountContext accountContext) =>
            accountContext.AccountId ?? accountContext.UserId!.Value;

        static IResult NotFoundBody() => Results.NotFound(BuildErrorBody(
            "job_posting_not_found",
            "The job posting was not found."));

        app.MapPost("/api/discovery/job-postings", async (
                SaveJobPostingRequest request,
                IValidator<SaveJobPostingRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await ManageJobPostingsHandler.CreateAsync(
                    request, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Created($"/api/discovery/job-postings/{response.Id}", response);
            })
            .RequirePermission(Permissions.JobPostings.Manage);

        app.MapGet("/api/discovery/job-postings", async (
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
                Results.Ok(await ManageJobPostingsHandler.ListAsync(
                    AccountOf(accountContext), dbContext, cancellationToken).ConfigureAwait(false)))
            .RequirePermission(Permissions.JobPostings.Manage);

        app.MapGet("/api/discovery/job-postings/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                var response = await ManageJobPostingsHandler.GetAsync(
                    id, AccountOf(accountContext), dbContext, cancellationToken).ConfigureAwait(false);
                return response is null ? NotFoundBody() : Results.Ok(response);
            })
            .RequirePermission(Permissions.JobPostings.Manage);

        app.MapPut("/api/discovery/job-postings/{id:guid}", async (
                Guid id,
                SaveJobPostingRequest request,
                IValidator<SaveJobPostingRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await ManageJobPostingsHandler.UpdateAsync(
                    id, request, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return response is null ? NotFoundBody() : Results.Ok(response);
            })
            .RequirePermission(Permissions.JobPostings.Manage);

        app.MapPost("/api/discovery/job-postings/{id:guid}/status", async (
                Guid id,
                ChangeJobPostingStatusRequest request,
                IValidator<ChangeJobPostingStatusRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await ManageJobPostingsHandler.ChangeStatusAsync(
                    id, request.Status!, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return response is null ? NotFoundBody() : Results.Ok(response);
            })
            .RequirePermission(Permissions.JobPostings.Manage);

        app.MapGet("/api/discovery/jobs", async (
                string? kind,
                string? workMode,
                string? q,
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                if (!string.IsNullOrWhiteSpace(kind) && !JobPostingKinds.All.Contains(kind))
                {
                    return Results.BadRequest(BuildErrorBody(
                        "job_posting_kind_invalid", "Kind must be Job or Internship."));
                }

                if (!string.IsNullOrWhiteSpace(workMode) && !JobPostingWorkModes.All.Contains(workMode))
                {
                    return Results.BadRequest(BuildErrorBody(
                        "job_posting_work_mode_invalid", "Work mode must be OnSite, Remote or Hybrid."));
                }

                return Results.Ok(await BrowseJobsHandler.ListAsync(
                    kind, workMode, q, AccountOf(accountContext), dbContext, cancellationToken).ConfigureAwait(false));
            })
            .RequirePermission(Permissions.JobPostings.Read);

        app.MapGet("/api/discovery/jobs/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                var response = await BrowseJobsHandler.GetAsync(id, AccountOf(accountContext), dbContext, cancellationToken)
                    .ConfigureAwait(false);
                return response is null ? NotFoundBody() : Results.Ok(response);
            })
            .RequirePermission(Permissions.JobPostings.Read);
    }

    /// <summary>STOR-67: Student applications (<c>job-applications:apply</c>) and Organization applicant
    /// review (<c>job-applications:review</c>).</summary>
    private static void MapJobApplicationEndpoints(WebApplication app)
    {
        static Guid AccountOf(IAccountContext accountContext) =>
            accountContext.AccountId ?? accountContext.UserId!.Value;

        static IResult PostingNotFound() => Results.NotFound(BuildErrorBody(
            "job_posting_not_found",
            "The job posting was not found."));

        static IResult ReviewFailure(ReviewApplicationsHandler.Failure failure) =>
            failure == ReviewApplicationsHandler.Failure.ApplicationNotFound
                ? Results.NotFound(BuildErrorBody("application_not_found", "The application was not found."))
                : PostingNotFound();

        app.MapPost("/api/discovery/jobs/{jobId:guid}/applications", async (
                Guid jobId,
                ApplyToJobRequest? request,
                IValidator<ApplyToJobRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                request ??= new ApplyToJobRequest();
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await ApplyToJobHandler.ApplyAsync(
                    jobId, request, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return response is null
                    ? PostingNotFound()
                    : Results.Created($"/api/discovery/applications/{response.Id}", response);
            })
            .RequirePermission(Permissions.JobApplications.Apply);

        app.MapGet("/api/discovery/applications", async (
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
                Results.Ok(await ApplyToJobHandler.ListOwnAsync(
                    AccountOf(accountContext), dbContext, cancellationToken).ConfigureAwait(false)))
            .RequirePermission(Permissions.JobApplications.Apply);

        app.MapGet("/api/discovery/job-postings/{id:guid}/applications", async (
                Guid id,
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                var result = await ReviewApplicationsHandler.ListAsync(
                    id, AccountOf(accountContext), dbContext, cancellationToken).ConfigureAwait(false);
                return result.IsSuccess ? Results.Ok(result.Value) : ReviewFailure(result.Failure);
            })
            .RequirePermission(Permissions.JobApplications.Review);

        app.MapGet("/api/discovery/job-postings/{id:guid}/applications/{applicationId:guid}", async (
                Guid id,
                Guid applicationId,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var result = await ReviewApplicationsHandler.GetAsync(
                    id, applicationId, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return result.IsSuccess ? Results.Ok(result.Value) : ReviewFailure(result.Failure);
            })
            .RequirePermission(Permissions.JobApplications.Review);

        app.MapPost("/api/discovery/job-postings/{id:guid}/applications/{applicationId:guid}/status", async (
                Guid id,
                Guid applicationId,
                ChangeApplicationStatusRequest request,
                IValidator<ChangeApplicationStatusRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var result = await ReviewApplicationsHandler.ChangeStatusAsync(
                    id, applicationId, request.Status!, AccountOf(accountContext), dbContext, auditLogWriter,
                    timeProvider, cancellationToken).ConfigureAwait(false);
                return result.IsSuccess ? Results.Ok(result.Value) : ReviewFailure(result.Failure);
            })
            .RequirePermission(Permissions.JobApplications.Review);
    }

    /// <summary>Build the standard two-field error body
    /// (<c>{ errorCode, message }</c>) without taking a dependency on
    /// <c>Storporate.Api.Errors.ErrorResponse</c>. Modules must not
    /// project-reference the API project — vertical-slice isolation —
    /// so the wire shape lives here as an anonymous object that JSON
    /// serializes verbatim to the same keys. The Keys are camelCase
    /// via <c>JsonSerializerDefaults.Web</c> which the framework's
    /// dependency-injected serializer already applies.</summary>
    private static object BuildErrorBody(string errorCode, string message) =>
        new { errorCode, message };

    /// <summary>Build the RFC 5987 <c>Content-Disposition</c> value for
    /// the streaming file response. The disposition kind is
    /// <c>inline</c> for the safe-inline content types and
    /// <c>attachment</c> for every other (including the
    /// <c>application/octet-stream</c> fallback). The file name has
    /// already been sanitized by the handler (path separators and
    /// control characters stripped) — the URL-encoding here preserves
    /// UTF-8 characters verbatim per RFC 5987's
    /// <c>filename*=UTF-8''&lt;url-encoded&gt;</c> syntax. A fallback
    /// <c>filename="original"</c> is also emitted so HTTP/1.0 clients
    /// that don't understand RFC 5987 still see something legible.</summary>
    private static string BuildContentDisposition(string dispositionKind, string fileName)
    {
        var encoded = Uri.EscapeDataString(fileName);
        return $"{dispositionKind}; filename=\"original\"; filename*=UTF-8''{encoded}";
    }
}
