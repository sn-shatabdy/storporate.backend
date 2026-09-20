using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.SearchableProfile;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Modules.DiscoveryHiring;

/// <summary>
/// Minimal-API endpoints for the DiscoveryHiring module — Phase 1 ships the
/// student-facing "let employers find me" toggle surface
/// (<c>GET</c> + <c>PUT /api/discovery/searchable-profile</c>). Phase 2 will
/// add the employer-side search endpoints on the same route group.
/// </summary>
/// <remarks>
/// <para>
/// <b>Permission gates.</b> Each endpoint calls
/// <c>.RequirePermission(Permissions.SearchableProfile.&lt;Verb&gt;)</c>.
/// The two new <see cref="Permissions.SearchableProfile"/> constants are
/// recognized by the <see cref="RequirePermissionAttribute"/> fail-fast
/// check because <see cref="Permissions.All"/> is rebuilt via reflection.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> Both handlers rely on the EF Core global query
/// filter on <see cref="SharedKernel.Entities.IAccountScoped"/> so the
/// reads are scoped to the ambient account automatically. A cross-account
/// PUT is impossible because the global query filter restricts the lookup
/// to the caller's row; the handler treats a missing row as the
/// first-time-PUT case.
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
    }
}
