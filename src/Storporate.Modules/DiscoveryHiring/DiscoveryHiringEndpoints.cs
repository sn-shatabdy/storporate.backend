using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.SearchableProfile;
using Storporate.Modules.DiscoveryHiring.TalentSearch;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Modules.DiscoveryHiring;

/// <summary>
/// Minimal-API endpoints for the DiscoveryHiring module. Phase 1 ships the
/// student-facing "let employers find me" toggle surface
/// (<c>GET</c> + <c>PUT /api/discovery/searchable-profile</c>); Phase 2
/// adds the employer-side plain-language talent-search endpoints on the
/// same route group.
/// </summary>
/// <remarks>
/// <para>
/// <b>Permission gates.</b> Each endpoint calls
/// <c>.RequirePermission(Permissions.&lt;Area&gt;.&lt;Verb&gt;)</c>. The
/// searchable-profile endpoints use <see cref="Permissions.SearchableProfile"/>;
/// the search endpoints use <see cref="Permissions.TalentSearch"/> (Create
/// for POST, Read for GET). The constants are recognized by the
/// <see cref="RequirePermissionAttribute"/> fail-fast check because
/// <see cref="Permissions.All"/> is rebuilt via reflection.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> All handlers rely on the EF Core global query
/// filter on <see cref="SharedKernel.Entities.IAccountScoped"/> so the
/// reads are scoped to the ambient account automatically. A cross-account
/// GET returns <c>404 talent_search_not_found</c> rather than <c>403</c>
/// — the row is invisible to the caller under the filter, indistinguishable
/// from a never-existed id.
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
    }
}
