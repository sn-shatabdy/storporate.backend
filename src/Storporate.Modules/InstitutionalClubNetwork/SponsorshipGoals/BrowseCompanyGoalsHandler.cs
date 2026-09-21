using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;

/// <summary>
/// Club-side read of Active company goal sets (STOR-70). Only Active rows are ever returned; the
/// id in every response is the goal set id, never the owner account id. The budget is shown only
/// when the company chose to (<see cref="SponsorshipGoalSet.ShowBudget"/>) and a bound exists.
/// </summary>
public static class BrowseCompanyGoalsHandler
{
    public const int MaxResults = 50;

    public static async Task<CompanyGoalListResponse> ListAsync(
        string? objective,
        string? eventKind,
        string? query,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var sets = dbContext.SponsorshipGoalSets
            .AsNoTracking()
            .Where(s => s.Status == SponsorshipGoalStatuses.Active);

        var q = query?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(q))
        {
            sets = sets.Where(s =>
                s.Name.ToLower().Contains(q)
                || s.CompanyName.ToLower().Contains(q)
                || s.Objectives.ToLower().Contains(q));
        }

        var o = objective?.Trim();
        if (!string.IsNullOrEmpty(o))
        {
            // The stored array holds JSON strings, so match the JSON-encoded quoted entry exactly.
            var needle = SponsorshipGoalOptions.Serialize(o).ToLowerInvariant();
            sets = sets.Where(s => s.Objectives.ToLower().Contains(needle));
        }

        var k = eventKind?.Trim();
        if (!string.IsNullOrEmpty(k))
        {
            var needle = SponsorshipGoalOptions.Serialize(k).ToLowerInvariant();
            sets = sets.Where(s => s.EventKinds.ToLower().Contains(needle));
        }

        var rows = await sets
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(MaxResults)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CompanyGoalListResponse(rows.Select(ToSummary).ToList());
    }

    public static async Task<CompanyGoalDetail?> GetAsync(
        Guid id,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var set = await dbContext.SponsorshipGoalSets
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.Status == SponsorshipGoalStatuses.Active, cancellationToken)
            .ConfigureAwait(false);
        return set is null
            ? null
            : new CompanyGoalDetail(
                set.Id,
                set.Name,
                set.CompanyName,
                SponsorshipGoalOptions.Deserialize<List<string>>(set.Objectives, []),
                ManageSponsorshipGoalsHandler.ToAudience(set),
                SponsorshipGoalOptions.Deserialize<List<string>>(set.EventKinds, []),
                ToPublicBudget(set),
                set.Notes,
                set.UpdatedAt);
    }

    private static CompanyGoalSummary ToSummary(SponsorshipGoalSet s) => new(
        s.Id,
        s.Name,
        s.CompanyName,
        SponsorshipGoalOptions.Deserialize<List<string>>(s.Objectives, []),
        SponsorshipGoalOptions.Deserialize<List<string>>(s.EventKinds, []),
        SponsorshipGoalOptions.Deserialize<List<string>>(s.AudienceFieldsOfStudy, []),
        ToPublicBudget(s));

    private static SponsorshipPublicBudgetResponse? ToPublicBudget(SponsorshipGoalSet s) =>
        s.ShowBudget && (s.BudgetMin is not null || s.BudgetMax is not null)
            ? new SponsorshipPublicBudgetResponse(s.BudgetMin, s.BudgetMax)
            : null;
}
