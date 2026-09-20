using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Responses;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>Reads a single <see cref="ExplorationComparison"/> owned by the
/// caller's account. Returns <see langword="null"/> when the id doesn't
/// match — the endpoint maps that to 404.</summary>
public static class GetExplorationComparisonHandler
{
    public static async Task<ExplorationComparisonResponse?> ExecuteAsync(
        Guid comparisonId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var comparison = await dbContext.ExplorationComparisons
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == comparisonId, cancellationToken)
            .ConfigureAwait(false);
        if (comparison is null)
        {
            return null;
        }

        return new ExplorationComparisonResponse(
            Id: comparison.Id,
            FirstExplorationId: comparison.FirstExplorationId,
            SecondExplorationId: comparison.SecondExplorationId,
            Status: comparison.Status,
            ResultText: comparison.ResultText,
            CreatedAt: comparison.CreatedAt);
    }
}
