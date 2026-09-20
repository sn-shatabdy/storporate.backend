using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Exceptions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>Enqueues an <see cref="GrowthJobTypes.AdvisorTurn"/> refresh job
/// for an existing exploration. The refresh path re-derives the summary
/// from the existing messages without adding a new student turn.
/// Rejects with <see cref="ExplorationBusyException"/> when the
/// exploration is currently <see cref="ExplorationStatuses.Working"/>.</summary>
public static class RefreshExplorationHandler
{
    public sealed record RefreshOutcome(bool Accepted, Guid ExplorationId, Guid NewJobId);

    public static async Task<RefreshOutcome> ExecuteAsync(
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
            return new RefreshOutcome(Accepted: false, ExplorationId: explorationId, NewJobId: Guid.Empty);
        }

        if (exploration.Status == ExplorationStatuses.Working)
        {
            throw new ExplorationBusyException(exploration.Id);
        }

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
                new AdvisorTurnPayload(exploration.Id, AdvisorTurnModes.Refresh)),
            AccountId = exploration.AccountId,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        };
        dbContext.Jobs.Add(job);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new RefreshOutcome(Accepted: true, ExplorationId: exploration.Id, NewJobId: job.Id);
    }
}
