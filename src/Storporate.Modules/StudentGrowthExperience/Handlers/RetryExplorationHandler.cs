using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Exceptions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>Re-queues an <see cref="GrowthJobTypes.AdvisorTurn"/> job for an
/// exploration whose previous turn exhausted its retry budget. The handler
/// rejects with <see cref="ExplorationNotRetryableException"/> when the
/// exploration is not in <see cref="ExplorationStatuses.Failed"/>.</summary>
/// <remarks>
/// <para>
/// <b>Mode-choice rule.</b> The retry's <see cref="AdvisorTurnPayload.Mode"/>
/// is derived from the exploration's current message history:
/// <list type="bullet">
/// <item>No messages at all — re-run as <see cref="AdvisorTurnModes.Opening"/>.</item>
/// <item>The latest message is <see cref="ExplorationRoles.Student"/> and is
/// newer than the latest <see cref="ExplorationRoles.Advisor"/> message —
/// run as <see cref="AdvisorTurnModes.Reply"/>.</item>
/// <item>Otherwise — run as <see cref="AdvisorTurnModes.Refresh"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Tenant isolation.</b> The exploration load runs under the EF global
/// query filter; a cross-account id never matches and the endpoint returns
/// a 404 (matching the delete + retry portfolio handler patterns).
/// </para>
/// </remarks>
public static class RetryExplorationHandler
{
    public sealed record RetryOutcome(bool Enqueued, Guid ExplorationId, Guid NewJobId);

    public static async Task<RetryOutcome> ExecuteAsync(
        Guid explorationId,
        WriteDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var exploration = await dbContext.Explorations
            .FirstOrDefaultAsync(e => e.Id == explorationId, cancellationToken)
            .ConfigureAwait(false);
        if (exploration is null)
        {
            return new RetryOutcome(Enqueued: false, ExplorationId: explorationId, NewJobId: Guid.Empty);
        }

        if (exploration.Status != ExplorationStatuses.Failed)
        {
            throw new ExplorationNotRetryableException(exploration.Status);
        }

        var mode = await DetermineRetryModeAsync(dbContext, exploration.Id, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        exploration.Status = ExplorationStatuses.Working;
        exploration.LastError = null;
        exploration.UpdatedAt = now;

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = GrowthJobTypes.AdvisorTurn,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            PayloadJson = JsonSerializer.Serialize(
                new AdvisorTurnPayload(exploration.Id, mode)),
            AccountId = exploration.AccountId,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        };
        dbContext.Jobs.Add(job);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new RetryOutcome(Enqueued: true, ExplorationId: exploration.Id, NewJobId: job.Id);
    }

    private static async Task<string> DetermineRetryModeAsync(
        WriteDbContext dbContext,
        Guid explorationId,
        CancellationToken cancellationToken)
    {
        var latestStudent = await dbContext.ExplorationMessages
            .Where(m => m.ExplorationId == explorationId && m.Role == ExplorationRoles.Student)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => (DateTimeOffset?)m.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (latestStudent is null)
        {
            return AdvisorTurnModes.Opening;
        }

        var latestAdvisor = await dbContext.ExplorationMessages
            .Where(m => m.ExplorationId == explorationId && m.Role == ExplorationRoles.Advisor)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => (DateTimeOffset?)m.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (latestAdvisor is null || latestStudent > latestAdvisor)
        {
            return AdvisorTurnModes.Reply;
        }

        return AdvisorTurnModes.Refresh;
    }
}
