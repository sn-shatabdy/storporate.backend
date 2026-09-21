using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;
using Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Modules.InstitutionalClubNetwork;

/// <summary>
/// STOR-69 endpoints: the club manages its own profile (<c>club-profile:manage</c>) and
/// companies browse Published profiles (<c>club-profile:read</c>). STOR-70 endpoints: the company
/// manages its own sponsorship goal sets (<c>sponsorship-goals:manage</c>) and clubs read the
/// Active ones (<c>sponsorship-goals:read</c>). Every endpoint declares
/// <c>.RequirePermission(...)</c>, as enforced by the architecture tests.
/// </summary>
public static class InstitutionalClubNetworkEndpoints
{
    public static void MapInstitutionalClubNetworkEndpoints(this WebApplication app)
    {
        static Guid AccountOf(IAccountContext accountContext) =>
            accountContext.AccountId ?? accountContext.UserId!.Value;

        static IResult NotFoundBody() => Results.NotFound(BuildErrorBody(
            "club_profile_not_found",
            "The club profile was not found."));

        app.MapGet("/api/clubs/profile", async (
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                var response = await ManageClubProfileHandler.GetOwnAsync(AccountOf(accountContext), dbContext, cancellationToken)
                    .ConfigureAwait(false);
                return response is null ? NotFoundBody() : Results.Ok(response);
            })
            .RequirePermission(Permissions.ClubProfiles.Manage);

        app.MapPut("/api/clubs/profile", async (
                SaveClubProfileRequest request,
                IValidator<SaveClubProfileRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await ManageClubProfileHandler.SaveAsync(
                    request, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(response);
            })
            .RequirePermission(Permissions.ClubProfiles.Manage);

        app.MapPost("/api/clubs/profile/publish", async (
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var response = await ManageClubProfileHandler.PublishAsync(
                    AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return response is null ? NotFoundBody() : Results.Ok(response);
            })
            .RequirePermission(Permissions.ClubProfiles.Manage);

        app.MapPost("/api/clubs/profile/unpublish", async (
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var response = await ManageClubProfileHandler.UnpublishAsync(
                    AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return response is null ? NotFoundBody() : Results.Ok(response);
            })
            .RequirePermission(Permissions.ClubProfiles.Manage);

        app.MapGet("/api/clubs", async (
                string? q,
                string? field,
                string? university,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
                Results.Ok(await BrowseClubsHandler.ListAsync(q, field, university, dbContext, cancellationToken)
                    .ConfigureAwait(false)))
            .RequirePermission(Permissions.ClubProfiles.Read);

        app.MapGet("/api/clubs/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var response = await BrowseClubsHandler.GetAsync(id, dbContext, cancellationToken).ConfigureAwait(false);
                return response is null ? NotFoundBody() : Results.Ok(response);
            })
            .RequirePermission(Permissions.ClubProfiles.Read);

        MapSponsorshipGoalEndpoints(app);
    }

    private static void MapSponsorshipGoalEndpoints(WebApplication app)
    {
        static Guid AccountOf(IAccountContext accountContext) =>
            accountContext.AccountId ?? accountContext.UserId!.Value;

        static IResult GoalNotFound() => Results.NotFound(BuildErrorBody(
            "sponsorship_goal_not_found",
            "The sponsorship goal set was not found."));

        // --- Company side (sponsorship-goals:manage), always scoped to the caller's own sets ---
        app.MapPost("/api/sponsorship/goals", async (
                SaveSponsorshipGoalRequest request,
                IValidator<SaveSponsorshipGoalRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await ManageSponsorshipGoalsHandler.CreateAsync(
                    request, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Created($"/api/sponsorship/goals/{response.Id}", response);
            })
            .RequirePermission(Permissions.SponsorshipGoals.Manage);

        app.MapGet("/api/sponsorship/goals", async (
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
                Results.Ok(await ManageSponsorshipGoalsHandler.ListOwnAsync(AccountOf(accountContext), dbContext, cancellationToken)
                    .ConfigureAwait(false)))
            .RequirePermission(Permissions.SponsorshipGoals.Manage);

        app.MapGet("/api/sponsorship/goals/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                var response = await ManageSponsorshipGoalsHandler.GetOwnAsync(id, AccountOf(accountContext), dbContext, cancellationToken)
                    .ConfigureAwait(false);
                return response is null ? GoalNotFound() : Results.Ok(response);
            })
            .RequirePermission(Permissions.SponsorshipGoals.Manage);

        app.MapPut("/api/sponsorship/goals/{id:guid}", async (
                Guid id,
                SaveSponsorshipGoalRequest request,
                IValidator<SaveSponsorshipGoalRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await ManageSponsorshipGoalsHandler.UpdateAsync(
                    id, request, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return response is null ? GoalNotFound() : Results.Ok(response);
            })
            .RequirePermission(Permissions.SponsorshipGoals.Manage);

        app.MapPost("/api/sponsorship/goals/{id:guid}/status", async (
                Guid id,
                SetSponsorshipGoalStatusRequest request,
                IValidator<SetSponsorshipGoalStatusRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var response = await ManageSponsorshipGoalsHandler.SetStatusAsync(
                    id, request.Status!, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return response is null ? GoalNotFound() : Results.Ok(response);
            })
            .RequirePermission(Permissions.SponsorshipGoals.Manage);

        app.MapDelete("/api/sponsorship/goals/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                var deleted = await ManageSponsorshipGoalsHandler.DeleteAsync(
                    id, AccountOf(accountContext), dbContext, auditLogWriter, cancellationToken)
                    .ConfigureAwait(false);
                return deleted ? Results.NoContent() : GoalNotFound();
            })
            .RequirePermission(Permissions.SponsorshipGoals.Manage);

        // --- Club side (sponsorship-goals:read), Active sets only ---
        app.MapGet("/api/sponsorship/companies", async (
                string? objective,
                string? eventKind,
                string? q,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
                Results.Ok(await BrowseCompanyGoalsHandler.ListAsync(objective, eventKind, q, dbContext, cancellationToken)
                    .ConfigureAwait(false)))
            .RequirePermission(Permissions.SponsorshipGoals.Read);

        app.MapGet("/api/sponsorship/companies/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var response = await BrowseCompanyGoalsHandler.GetAsync(id, dbContext, cancellationToken).ConfigureAwait(false);
                return response is null ? GoalNotFound() : Results.Ok(response);
            })
            .RequirePermission(Permissions.SponsorshipGoals.Read);
    }

    /// <summary>Standard <c>{ errorCode, message }</c> body; modules must not reference the API project.</summary>
    private static object BuildErrorBody(string errorCode, string message) => new { errorCode, message };
}
