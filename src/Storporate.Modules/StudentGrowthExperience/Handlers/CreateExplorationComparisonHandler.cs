using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Exceptions;
using Storporate.Modules.StudentGrowthExperience.Requests;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>Creates a new <see cref="ExplorationComparison"/> between two of
/// the caller's explorations and enqueues a
/// <see cref="GrowthJobTypes.CompareExplorations"/> job. Rejects with
/// <see cref="ExplorationHasNoSummaryException"/> when either side has not
/// yet produced an <see cref="ExplorationSummaryVersion"/>.</summary>
/// <remarks>
/// <para>
/// <b>404 on cross-account ids.</b> The validator has already rejected the
/// "same id twice" case before this handler runs. Each exploration load
/// runs under the EF global query filter — a cross-account id never
/// matches and the handler returns <see cref="ComparisonCreateOutcome.Accepted"/>
/// = <see langword="false"/> so the endpoint maps to a 404 (mirrors the
/// portfolio delete handler pattern, never 403).
/// </para>
/// </remarks>
public static class CreateExplorationComparisonHandler
{
    public sealed record ComparisonCreateOutcome(bool Accepted, Guid ComparisonId);

    public static async Task<ComparisonCreateOutcome> ExecuteAsync(
        CreateExplorationComparisonRequest request,
        WriteDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var first = await dbContext.Explorations
            .FirstOrDefaultAsync(e => e.Id == request.FirstExplorationId, cancellationToken)
            .ConfigureAwait(false);
        var second = await dbContext.Explorations
            .FirstOrDefaultAsync(e => e.Id == request.SecondExplorationId, cancellationToken)
            .ConfigureAwait(false);

        if (first is null || second is null)
        {
            return new ComparisonCreateOutcome(Accepted: false, ComparisonId: Guid.Empty);
        }

        if (first.AccountId != second.AccountId)
        {
            // Cross-account comparison request — never matches a row owned by
            // the caller (the global filter hides the other account's row),
            // so this branch is effectively unreachable when the filter is on.
            // Keep the explicit guard so a future misconfiguration is caught
            // by a clear exception rather than a silent save-time interceptor
            // failure.
            return new ComparisonCreateOutcome(Accepted: false, ComparisonId: Guid.Empty);
        }

        await EnsureHasSummaryAsync(dbContext, first.Id, cancellationToken).ConfigureAwait(false);
        await EnsureHasSummaryAsync(dbContext, second.Id, cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var comparison = new ExplorationComparison
        {
            Id = Guid.NewGuid(),
            AccountId = first.AccountId,
            FirstExplorationId = first.Id,
            SecondExplorationId = second.Id,
            Status = ExplorationComparisonStatuses.Pending,
            ResultText = null,
            CreatedAt = now,
        };
        dbContext.ExplorationComparisons.Add(comparison);

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = GrowthJobTypes.CompareExplorations,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            PayloadJson = JsonSerializer.Serialize(new CompareExplorationsPayload(comparison.Id)),
            AccountId = first.AccountId,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        };
        dbContext.Jobs.Add(job);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ComparisonCreateOutcome(Accepted: true, ComparisonId: comparison.Id);
    }

    private static async Task EnsureHasSummaryAsync(
        WriteDbContext dbContext,
        Guid explorationId,
        CancellationToken cancellationToken)
    {
        var hasSummary = await dbContext.ExplorationSummaryVersions
            .AnyAsync(s => s.ExplorationId == explorationId, cancellationToken)
            .ConfigureAwait(false);
        if (!hasSummary)
        {
            throw new ExplorationHasNoSummaryException(explorationId);
        }
    }
}
