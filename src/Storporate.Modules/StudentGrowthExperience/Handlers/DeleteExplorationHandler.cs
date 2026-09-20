using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>Hard-deletes a single <see cref="Exploration"/> and its
/// dependent rows (<see cref="ExplorationMessage"/>s,
/// <see cref="ExplorationSummaryVersion"/>s,
/// <see cref="StudentContextNote"/>s scoped to this exploration,
/// <see cref="ExplorationComparison"/>s touching it). Writes one
/// <c>"exploration_deleted"</c> audit row carrying ids + counts only —
/// never the message text.</summary>
/// <remarks>
/// <para>
/// <b>Single SaveChanges.</b> All child rows are removed in the same
/// <c>SaveChangesAsync</c> as the parent so a half-committed state is
/// impossible. The <c>ToListAsync + RemoveRange</c> pattern matches the
/// portfolio delete handler — keeps the EF InMemory test provider happy
/// (which doesn't implement <c>ExecuteDeleteAsync</c>).
/// </para>
/// <para>
/// <b>Cross-exploration notes.</b> <see cref="StudentContextNote"/> rows
/// are scoped by <c>ExplorationId</c> when set, so only the notes whose
/// <c>ExplorationId</c> equals this exploration are removed. Cross-
/// exploration notes (the <c>ExplorationId</c>-null rows or rows owned by
/// a different exploration) are left intact.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> The exploration load runs under the EF global
/// query filter; a cross-account id never matches and the endpoint returns
/// a 404 (matching the portfolio delete handler pattern).
/// </para>
/// </remarks>
public static class DeleteExplorationHandler
{
    public static async Task<bool> ExecuteAsync(
        Guid explorationId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(auditLogWriter);

        var exploration = await dbContext.Explorations
            .FirstOrDefaultAsync(e => e.Id == explorationId, cancellationToken)
            .ConfigureAwait(false);
        if (exploration is null)
        {
            return false;
        }

        var messages = await dbContext.ExplorationMessages
            .Where(m => m.ExplorationId == explorationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var messageCount = messages.Count;
        dbContext.ExplorationMessages.RemoveRange(messages);

        var summaryVersions = await dbContext.ExplorationSummaryVersions
            .Where(s => s.ExplorationId == explorationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var summaryVersionCount = summaryVersions.Count;
        dbContext.ExplorationSummaryVersions.RemoveRange(summaryVersions);

        var contextNotes = await dbContext.StudentContextNotes
            .Where(n => n.ExplorationId == explorationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.StudentContextNotes.RemoveRange(contextNotes);

        var comparisons = await dbContext.ExplorationComparisons
            .Where(c => c.FirstExplorationId == explorationId || c.SecondExplorationId == explorationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.ExplorationComparisons.RemoveRange(comparisons);

        dbContext.Explorations.Remove(exploration);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var auditMetadata =
            $$"""{"messageCount":{{messageCount}},"summaryVersionCount":{{summaryVersionCount}},"comparisonCount":{{comparisons.Count}}}""";
        await auditLogWriter.WriteAsync(
            action: "exploration_deleted",
            resourceType: "Exploration",
            resourceId: explorationId.ToString(),
            metadataJson: auditMetadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return true;
    }
}
