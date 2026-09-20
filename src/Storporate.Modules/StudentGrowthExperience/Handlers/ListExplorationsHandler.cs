using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Responses;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>Lists the caller's <see cref="Exploration"/> rows ordered by
/// <see cref="Exploration.UpdatedAt"/> descending. Tenant-isolated via the
/// EF Core global query filter — a row owned by another account simply does
/// not match the filter and never appears in the response.</summary>
public static class ListExplorationsHandler
{
    public static async Task<IReadOnlyList<ExplorationListItemResponse>> ExecuteAsync(
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Project via a per-exploration GroupBy so the latest version number
        // is computed in the database rather than re-fetched in application
        // code. Both queries run under the global query filter; the join key
        // (ExplorationId) plus the per-row max keeps the projection scoped to
        // the caller's account.
        var rows = await dbContext.Explorations
            .AsNoTracking()
            .OrderByDescending(e => e.UpdatedAt)
            .Select(e => new
            {
                e.Id,
                e.Title,
                e.Status,
                e.UpdatedAt,
                LatestVersionNumber = dbContext.ExplorationSummaryVersions
                    .Where(s => s.ExplorationId == e.Id)
                    .Max(s => (int?)s.VersionNumber),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(r => new ExplorationListItemResponse(
                Id: r.Id,
                Title: r.Title,
                Status: r.Status,
                UpdatedAt: r.UpdatedAt,
                LatestVersionNumber: r.LatestVersionNumber))
            .ToList();
    }
}
