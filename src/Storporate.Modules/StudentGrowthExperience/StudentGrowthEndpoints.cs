using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Handlers;
using Storporate.Modules.StudentGrowthExperience.Requests;
using Storporate.Modules.StudentGrowthExperience.Validators;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Modules.StudentGrowthExperience;

/// <summary>
/// Minimal-API endpoints for the StudentGrowthExperience module — the
/// advisor's student-facing surface (open an exploration, reply, refresh,
/// retry, rename, delete, compare two). Registered once from
/// <c>Program.cs</c> via <c>app.MapStudentGrowthExperienceEndpoints()</c>
/// alongside the other module endpoint maps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Permission gates.</b> Each endpoint calls
/// <c>.RequirePermission(Permissions.Advisor.&lt;Verb&gt;)</c>, mirroring the
/// established pattern from the Portfolio module's
/// <see cref="Storporate.Modules.Portfolio.PortfolioEndpoints"/>. The four
/// <see cref="Permissions.Advisor"/> constants are recognized by the
/// <see cref="RequirePermissionAttribute"/> fail-fast check because
/// <see cref="Permissions.All"/> is rebuilt via reflection.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> Every read / write runs through the EF Core
/// global query filter installed in <c>WriteDbContext.OnModelCreating</c>
/// for every <see cref="Storporate.SharedKernel.Entities.IAccountScoped"/>
/// entity. Cross-account ids never match the filter and the handlers
/// return <see langword="false"/> so the endpoints map to 404 — same
/// precedent as <see cref="Storporate.Modules.Portfolio.DeletePortfolioItemHandler"/>.
/// </para>
/// </remarks>
public static class StudentGrowthEndpoints
{
    public static void MapStudentGrowthExperienceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/growth/explorations", async (
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var items = await ListExplorationsHandler.ExecuteAsync(dbContext, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(items);
            })
            .RequirePermission(Permissions.Advisor.Read);

        app.MapPost("/api/growth/explorations", async (
                CreateExplorationRequest request,
                IValidator<CreateExplorationRequest> validator,
                WriteDbContext dbContext,
                IOptions<AdvisorOptions> advisorOptions,
                IAccountContext accountContext,
                IAuditLogWriter auditLogWriter,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await CreateExplorationHandler.ExecuteAsync(
                    request, dbContext, advisorOptions, accountContext,
                    auditLogWriter, timeProvider, cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/growth/explorations/{response.Id}", response);
            })
            .RequirePermission(Permissions.Advisor.Create);

        app.MapGet("/api/growth/explorations/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var detail = await GetExplorationHandler.ExecuteAsync(id, dbContext, cancellationToken)
                    .ConfigureAwait(false);
                return detail is null ? Results.NotFound() : Results.Ok(detail);
            })
            .RequirePermission(Permissions.Advisor.Read);

        app.MapPost("/api/growth/explorations/{id:guid}/messages", async (
                Guid id,
                AddExplorationMessageRequest request,
                IValidator<AddExplorationMessageRequest> validator,
                WriteDbContext dbContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var outcome = await AddExplorationMessageHandler.ExecuteAsync(
                    id, request, dbContext, timeProvider, cancellationToken).ConfigureAwait(false);
                return outcome.Accepted
                    ? Results.Accepted(value: new
                    {
                        explorationId = outcome.ExplorationId,
                        newJobId = outcome.NewJobId,
                    })
                    : Results.NotFound();
            })
            .RequirePermission(Permissions.Advisor.Update);

        app.MapPost("/api/growth/explorations/{id:guid}/refresh", async (
                Guid id,
                WriteDbContext dbContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var outcome = await RefreshExplorationHandler.ExecuteAsync(
                    id, dbContext, timeProvider, cancellationToken).ConfigureAwait(false);
                return outcome.Accepted
                    ? Results.Accepted(value: new
                    {
                        explorationId = outcome.ExplorationId,
                        newJobId = outcome.NewJobId,
                    })
                    : Results.NotFound();
            })
            .RequirePermission(Permissions.Advisor.Update);

        app.MapPost("/api/growth/explorations/{id:guid}/retry", async (
                Guid id,
                WriteDbContext dbContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var outcome = await RetryExplorationHandler.ExecuteAsync(
                    id, dbContext, timeProvider, cancellationToken).ConfigureAwait(false);
                return outcome.Enqueued
                    ? Results.Accepted(value: new
                    {
                        explorationId = outcome.ExplorationId,
                        newJobId = outcome.NewJobId,
                    })
                    : Results.NotFound();
            })
            .RequirePermission(Permissions.Advisor.Update);

        app.MapPut("/api/growth/explorations/{id:guid}/title", async (
                Guid id,
                UpdateExplorationTitleRequest request,
                IValidator<UpdateExplorationTitleRequest> validator,
                WriteDbContext dbContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var outcome = await UpdateExplorationTitleHandler.ExecuteAsync(
                    id, request, dbContext, timeProvider, cancellationToken).ConfigureAwait(false);
                return outcome.Updated ? Results.NoContent() : Results.NotFound();
            })
            .RequirePermission(Permissions.Advisor.Update);

        app.MapDelete("/api/growth/explorations/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                CancellationToken cancellationToken) =>
            {
                var deleted = await DeleteExplorationHandler.ExecuteAsync(
                    id, dbContext, auditLogWriter, cancellationToken).ConfigureAwait(false);
                return deleted ? Results.NoContent() : Results.NotFound();
            })
            .RequirePermission(Permissions.Advisor.Delete);

        app.MapPost("/api/growth/explorations/compare", async (
                CreateExplorationComparisonRequest request,
                IValidator<CreateExplorationComparisonRequest> validator,
                WriteDbContext dbContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var outcome = await CreateExplorationComparisonHandler.ExecuteAsync(
                    request, dbContext, timeProvider, cancellationToken).ConfigureAwait(false);
                return outcome.Accepted
                    ? Results.Accepted(value: new
                    {
                        comparisonId = outcome.ComparisonId,
                    })
                    : Results.NotFound();
            })
            .RequirePermission(Permissions.Advisor.Update);

        app.MapGet("/api/growth/explorations/compare/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var comparison = await GetExplorationComparisonHandler.ExecuteAsync(
                    id, dbContext, cancellationToken).ConfigureAwait(false);
                return comparison is null ? Results.NotFound() : Results.Ok(comparison);
            })
            .RequirePermission(Permissions.Advisor.Read);
    }
}
