using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Requests;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>Updates the <see cref="Exploration.Title"/> field. The
/// validator has already enforced 1-200 characters after trim before this
/// handler runs.</summary>
public static class UpdateExplorationTitleHandler
{
    /// <summary>The outcome of <see cref="ExecuteAsync"/>. <c>Updated = false</c>
/// maps to 404; <c>Updated = true</c> maps to 204.</summary>
    public sealed record UpdateOutcome(bool Updated, Guid ExplorationId);

    public static async Task<UpdateOutcome> ExecuteAsync(
        Guid explorationId,
        UpdateExplorationTitleRequest request,
        WriteDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var exploration = await dbContext.Explorations
            .FirstOrDefaultAsync(e => e.Id == explorationId, cancellationToken)
            .ConfigureAwait(false);
        if (exploration is null)
        {
            return new UpdateOutcome(Updated: false, ExplorationId: explorationId);
        }

        exploration.Title = request.Title!.Trim();
        exploration.UpdatedAt = timeProvider.GetUtcNow();

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new UpdateOutcome(Updated: true, ExplorationId: exploration.Id);
    }
}
