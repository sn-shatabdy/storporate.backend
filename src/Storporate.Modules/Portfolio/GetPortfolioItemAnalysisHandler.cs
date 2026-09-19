using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio.Analysis;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Reads the STOR-38 analysis state for a single <see cref="PortfolioItem"/> owned by the
/// caller's account: the per-item <see cref="PortfolioAnalysisStatuses"/> value, the most
/// recent successful analysis timestamp, the latest failure error message (when the item
/// is <see cref="PortfolioAnalysisStatuses.Failed"/>), and the AI-derived skill findings
/// the worker wrote for the item. Returns <see langword="null"/> when the item is
/// invisible to the caller's account scope so the endpoint maps to a clean 404 — the
/// established tenant-isolation precedent for this module (see
/// <see cref="DeletePortfolioItemHandler"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no manual <c>AccountId</c> filter.</b> Both <see cref="PortfolioItem"/> and
/// <see cref="PortfolioSkillFinding"/> implement <see cref="IAccountScoped"/>, so the
/// EF Core global query filter installed in <see cref="WriteDbContext.OnModelCreating"/>
/// restricts every read to the ambient account. Adding a manual
/// <c>e =&gt; e.AccountId == ...</c> predicate would be redundant noise on top of the
/// security-critical filter that is already running — same reasoning
/// <see cref="ListPortfolioItemsHandler"/> applies for the list endpoint.
/// </para>
/// <para>
/// <b>How the failure error message is sourced.</b> A
/// <see cref="PortfolioAnalysisStatuses.Failed"/> item's
/// <see cref="PortfolioItem.AnalysisStatus"/> carries no error text of its own — only the
/// most recent <see cref="Job"/> row of type <see cref="PortfolioJobTypes.AnalyzePortfolioItem"/>
/// for the item carries <see cref="Job.ErrorMessage"/>. We locate it by deserializing the
/// job payload's <see cref="AnalyzePortfolioItemPayload.PortfolioItemId"/> and ordering by
/// <see cref="Job.UpdatedAt"/> descending so the freshest failure surfaces. The job query
/// is also account-scoped via <see cref="IAccountScoped"/>, so a cross-account id
/// cannot leak another tenant's error text through this endpoint.
/// </para>
/// <para>
/// <b>Why <c>AsNoTracking</c>.</b> The handler only reads — it never writes back to
/// <see cref="WriteDbContext"/> — so the EF change tracker is pure overhead. Same
/// reasoning as <see cref="ListPortfolioItemsHandler"/>.
/// </para>
/// </remarks>
public static class GetPortfolioItemAnalysisHandler
{
    /// <summary>The shape of one analysis record fetched for the read side. Held
    /// inside this file because only the handler needs it — not part of the public
    /// wire contract (the JSON shape is flattened into
    /// <see cref="GetPortfolioItemAnalysisResponse"/> before the response leaves the
    /// server).</summary>
    public sealed record AnalysisReadModel(
        string Status,
        DateTimeOffset? LastAnalyzedAt,
        string? ErrorMessage,
        IReadOnlyList<PortfolioSkillFindingResponse> Skills);

    public static async Task<AnalysisReadModel?> ExecuteAsync(
        Guid portfolioItemId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Tenant isolation: the global query filter restricts the lookup to the
        // caller's account, so a cross-account id never matches and the handler
        // returns null (the endpoint maps that to a 404, not a 403, to avoid
        // confirming another account's portfolio id exists — same precedent as
        // DeletePortfolioItemHandler).
        var item = await dbContext.PortfolioItems
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == portfolioItemId, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            return null;
        }

        var findings = await dbContext.PortfolioSkillFindings
            .AsNoTracking()
            .Where(finding => finding.PortfolioItemId == portfolioItemId)
            .OrderBy(finding => finding.CreatedAt)
            .ThenBy(finding => finding.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var skills = findings
            .Select(f => new PortfolioSkillFindingResponse(
                SkillName: f.SkillName,
                ConfidenceBand: f.ConfidenceBand,
                Explanation: f.Explanation))
            .ToArray();

        var errorMessage = item.AnalysisStatus == PortfolioAnalysisStatuses.Failed
            ? await FindLatestFailureErrorMessageAsync(portfolioItemId, dbContext, cancellationToken)
                .ConfigureAwait(false)
            : null;

        return new AnalysisReadModel(
            Status: item.AnalysisStatus,
            LastAnalyzedAt: item.LastAnalyzedAt,
            ErrorMessage: errorMessage,
            Skills: skills);
    }

    /// <summary>
    /// Locates the most recent <see cref="JobStatus.Failed"/> job's
    /// <see cref="Job.ErrorMessage"/> for an analysis job targeting the given item.
    /// Returns <see langword="null"/> when no failed job exists (the item reached
    /// <see cref="PortfolioAnalysisStatuses.Failed"/> via a non-job code path, which
    /// can't happen today but is a defensive default).
    /// </summary>
    /// <remarks>
    /// Mirrors how <see cref="PortfolioAnalysisJobProcessor.FailJobAsync"/> and
    /// <see cref="PortfolioAnalysisJobProcessor.HandleRetryableFailureAsync"/> write the
    /// terminal <see cref="JobStatus.Failed"/> row's <see cref="Job.ErrorMessage"/>:
    /// the payload's <see cref="AnalyzePortfolioItemPayload.PortfolioItemId"/> identifies
    /// which item the job is for. We filter on Type, deserialize the payload to a
    /// strongly-typed record, and match on the id. <see cref="Job"/> is
    /// <see cref="IAccountScoped"/> too, so this query is tenant-isolated for free.
    /// </remarks>
    private static async Task<string?> FindLatestFailureErrorMessageAsync(
        Guid portfolioItemId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // Pull the most recent jobs of our type for this account; deserialize payloads
        // to filter down to the one for our item, then pick the freshest Failed row.
        // The query is bounded by account-scoped filtering + by the relatively small
        // number of jobs per account (the typical case is < 3 attempts then terminal),
        // so a scan over a few rows is the right shape — no need for an index hint here.
        var recentJobs = await dbContext.Jobs
            .AsNoTracking()
            .Where(job => job.Type == PortfolioJobTypes.AnalyzePortfolioItem
                && job.Status == JobStatus.Failed)
            .OrderByDescending(job => job.UpdatedAt)
            .Take(20)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var job in recentJobs)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<AnalyzePortfolioItemPayload>(job.PayloadJson);
                if (payload is not null && payload.PortfolioItemId == portfolioItemId)
                {
                    return job.ErrorMessage;
                }
            }
            catch (JsonException)
            {
                // A malformed payload would be FailJobAsync'd with an explicit
                // message; ignore the row and continue to the next candidate.
                continue;
            }
        }

        return null;
    }
}
