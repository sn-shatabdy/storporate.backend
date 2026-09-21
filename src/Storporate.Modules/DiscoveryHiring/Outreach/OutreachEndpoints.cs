using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Modules.DiscoveryHiring.Outreach;

/// <summary>
/// STOR-68 endpoints: the employer shortlist and outreach surface (<c>outreach:send</c>) and the
/// student inbox (<c>outreach:respond</c>). Foreign ids and unknown ids both answer 404.
/// </summary>
public static class OutreachEndpoints
{
    public static void MapOutreachEndpoints(this WebApplication app)
    {
        static Guid AccountOf(IAccountContext accountContext) =>
            accountContext.AccountId ?? accountContext.UserId!.Value;

        static IResult CandidateNotFound() => Results.NotFound(new
        {
            errorCode = "candidate_not_found",
            message = "That candidate is not available.",
        });

        static IResult ConversationNotFound() => Results.NotFound(new
        {
            errorCode = "outreach_not_found",
            message = "The conversation was not found.",
        });

        static IResult Detail(OutreachHandler.Result<ConversationDetailResponse> result, int successStatus) =>
            result.IsSuccess
                ? Results.Json(result.Value, statusCode: successStatus)
                : result.Failure == OutreachHandler.Failure.CandidateNotFound
                    ? CandidateNotFound()
                    : ConversationNotFound();

        // ------------------------------------------------------------ employer

        app.MapPost("/api/discovery/shortlist", async (
                SaveShortlistRequest request,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var result = await ShortlistHandler.AddAsync(
                    request.CandidateId, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return result.IsSuccess
                    ? Results.Json(result.Value, statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK)
                    : CandidateNotFound();
            })
            .RequirePermission(Permissions.Outreach.Send);

        app.MapGet("/api/discovery/shortlist", async (
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
                Results.Ok(await ShortlistHandler.ListAsync(AccountOf(accountContext), dbContext, cancellationToken)
                    .ConfigureAwait(false)))
            .RequirePermission(Permissions.Outreach.Send);

        app.MapDelete("/api/discovery/shortlist/{candidateId:guid}", async (
                Guid candidateId,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                await ShortlistHandler.RemoveAsync(
                    candidateId, AccountOf(accountContext), dbContext, auditLogWriter, cancellationToken)
                    .ConfigureAwait(false);
                return Results.NoContent();
            })
            .RequirePermission(Permissions.Outreach.Send);

        app.MapPost("/api/discovery/outreach", async (
                InviteCandidateRequest request,
                IValidator<InviteCandidateRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var result = await OutreachHandler.InviteAsync(
                    request, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return Detail(result, StatusCodes.Status201Created);
            })
            .RequirePermission(Permissions.Outreach.Send);

        app.MapGet("/api/discovery/outreach", async (
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
                Results.Ok(await OutreachHandler.ListForOrganizationAsync(
                    AccountOf(accountContext), dbContext, cancellationToken).ConfigureAwait(false)))
            .RequirePermission(Permissions.Outreach.Send);

        app.MapGet("/api/discovery/outreach/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
                Detail(
                    await OutreachHandler.GetForOrganizationAsync(id, AccountOf(accountContext), dbContext, cancellationToken)
                        .ConfigureAwait(false),
                    StatusCodes.Status200OK))
            .RequirePermission(Permissions.Outreach.Send);

        app.MapPost("/api/discovery/outreach/{id:guid}/messages", async (
                Guid id,
                SendOutreachMessageRequest request,
                IValidator<SendOutreachMessageRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var result = await OutreachHandler.SendAsOrganizationAsync(
                    id, request.Message!, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return Detail(result, StatusCodes.Status201Created);
            })
            .RequirePermission(Permissions.Outreach.Send);

        // ------------------------------------------------------------- student

        app.MapGet("/api/discovery/inbox", async (
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
                Results.Ok(await OutreachHandler.ListForStudentAsync(
                    AccountOf(accountContext), dbContext, cancellationToken).ConfigureAwait(false)))
            .RequirePermission(Permissions.Outreach.Respond);

        app.MapGet("/api/discovery/inbox/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
                Detail(
                    await OutreachHandler.GetForStudentAsync(id, AccountOf(accountContext), dbContext, cancellationToken)
                        .ConfigureAwait(false),
                    StatusCodes.Status200OK))
            .RequirePermission(Permissions.Outreach.Respond);

        app.MapPost("/api/discovery/inbox/{id:guid}/reply", async (
                Guid id,
                SendOutreachMessageRequest request,
                IValidator<SendOutreachMessageRequest> validator,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);
                var result = await OutreachHandler.ReplyAsync(
                    id, request.Message!, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return Detail(result, StatusCodes.Status200OK);
            })
            .RequirePermission(Permissions.Outreach.Respond);

        app.MapPost("/api/discovery/inbox/{id:guid}/decline", async (
                Guid id,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                IAccountContext accountContext,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
                Detail(
                    await OutreachHandler.DeclineAsync(
                        id, AccountOf(accountContext), dbContext, auditLogWriter, timeProvider, cancellationToken)
                        .ConfigureAwait(false),
                    StatusCodes.Status200OK))
            .RequirePermission(Permissions.Outreach.Respond);
    }
}
