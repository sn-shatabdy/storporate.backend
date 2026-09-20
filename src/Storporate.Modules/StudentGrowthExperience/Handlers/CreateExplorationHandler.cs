using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Exceptions;
using Storporate.Modules.StudentGrowthExperience.Requests;
using Storporate.Modules.StudentGrowthExperience.Responses;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>
/// Creates a new <see cref="Exploration"/> for the caller's account and
/// enqueues an <see cref="GrowthJobTypes.AdvisorTurn"/> opening-turn job.
/// The exploration starts in <see cref="ExplorationStatuses.Working"/> so
/// the UI's status badge reflects "queued" the moment the request
/// returns; the opening-turn job will flip it to
/// <see cref="ExplorationStatuses.Idle"/> on success or
/// <see cref="ExplorationStatuses.Failed"/> on retry exhaustion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cap enforcement.</b> The handler counts the account's existing
/// <see cref="Exploration"/> rows and throws
/// <see cref="ExplorationLimitReachedException"/> when the count is at
/// <see cref="AdvisorOptions.MaxExplorationsPerStudent"/>. The cap keeps
/// the per-student open-conversation count bounded.
/// </para>
/// <para>
/// <b>Atomic write.</b> The exploration row, the optional first
/// <see cref="ExplorationMessage"/>, and the <see cref="Job"/> row all
/// commit in a single <c>SaveChangesAsync</c>. A half-committed state
/// (exploration without its job, or vice versa) is impossible — matching
/// the create-portfolio-item pattern.
/// </para>
/// <para>
/// <b>Audit.</b> The handler writes one <c>"exploration_created"</c> audit
/// row carrying the exploration id, the account id, and whether an opening
/// direction was supplied — ids and a boolean only, never the message text
/// itself, mirroring the delete-portfolio-item handler's audit-metadata rule.
/// </para>
/// </remarks>
public static class CreateExplorationHandler
{
    public static async Task<CreateExplorationResponse> ExecuteAsync(
        CreateExplorationRequest request,
        WriteDbContext dbContext,
        IOptions<AdvisorOptions> advisorOptions,
        IAccountContext accountContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(advisorOptions);
        ArgumentNullException.ThrowIfNull(accountContext);
        ArgumentNullException.ThrowIfNull(auditLogWriter);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var advisorOptionsValue = advisorOptions.Value;
        ArgumentNullException.ThrowIfNull(advisorOptionsValue);

        if (!accountContext.UserId.HasValue)
        {
            throw new InvalidOperationException(
                "Cannot create an exploration without an ambient account context.");
        }

        var accountId = accountContext.AccountId ?? accountContext.UserId.Value;

        var existingCount = await dbContext.Explorations
            .Where(e => e.AccountId == accountId)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existingCount >= advisorOptionsValue.MaxExplorationsPerStudent)
        {
            throw new ExplorationLimitReachedException(advisorOptionsValue.MaxExplorationsPerStudent);
        }

        var now = timeProvider.GetUtcNow();
        var exploration = new Exploration
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = AdvisorDefaults.DefaultExplorationTitle,
            Status = ExplorationStatuses.Working,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Explorations.Add(exploration);

        var hasDirection = !string.IsNullOrWhiteSpace(request.Direction);
        if (hasDirection)
        {
            dbContext.ExplorationMessages.Add(new ExplorationMessage
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ExplorationId = exploration.Id,
                Role = ExplorationRoles.Student,
                Content = request.Direction!.Trim(),
                QuestionsJson = null,
                CreatedAt = now,
            });
        }

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = GrowthJobTypes.AdvisorTurn,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            PayloadJson = JsonSerializer.Serialize(
                new AdvisorTurnPayload(exploration.Id, AdvisorTurnModes.Opening)),
            AccountId = accountId,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        };
        dbContext.Jobs.Add(job);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var auditMetadata =
            $$"""{"hasOpeningDirection":{{(hasDirection ? "true" : "false")}},"jobId":{{System.Text.Json.JsonSerializer.Serialize(job.Id.ToString())}}}""";
        await auditLogWriter.WriteAsync(
            action: "exploration_created",
            resourceType: "Exploration",
            resourceId: exploration.Id.ToString(),
            metadataJson: auditMetadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CreateExplorationResponse(
            Id: exploration.Id,
            Status: ExplorationStatuses.Working);
    }
}
