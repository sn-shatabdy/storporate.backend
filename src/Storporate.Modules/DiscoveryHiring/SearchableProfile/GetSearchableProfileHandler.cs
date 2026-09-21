using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.SearchableProfile;

/// <summary>
/// Reads the calling student's <see cref="StudentSearchProfile"/> (or returns
/// a sensible default when the student has never toggled the feature) and
/// returns the <see cref="SearchableProfileResponse"/> wire shape. The
/// <see cref="SearchableProfileResponse.VisibleItemCount"/> is the count of
/// analyzed items with Strong/Developing findings — the same set the refresh
/// processor would copy into the <see cref="TalentIndexEntry"/> projection
/// if the student is (or just became) searchable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-state contract.</b> When the row is absent the response is
/// <c>IsSearchable = false</c>, an empty display name, all show flags on,
/// and <see cref="SearchableProfileResponse.VisibleItemCount"/> computed
/// from the live analyzed-items count. This keeps the FE's "first-ever
/// GET" path identical to "GET after opt-out" — the page renders the same
/// off-state with the same preview-card count.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> The lookup runs through the global query filter
/// on <see cref="IAccountScoped"/> so a cross-account id never matches;
/// <c>FirstOrDefaultAsync</c> returns null and the handler falls into the
/// default-state branch. (Student B can never read student A's profile.)
/// </para>
/// </remarks>
public static class GetSearchableProfileHandler
{
    public static async Task<SearchableProfileResponse> ExecuteAsync(
        WriteDbContext dbContext,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var profile = await dbContext.StudentSearchProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);

        var visibleItemCount = await CountQualifyingItemsAsync(dbContext, accountId, cancellationToken)
            .ConfigureAwait(false);

        if (profile is null)
        {
            // Default-state response for a student who has never toggled the
            // feature. DisplayName is the empty string (the validator
            // rejects a blank name when opting in; the row stays absent
            // until the student PUTs IsSearchable=true).
            return new SearchableProfileResponse(
                IsSearchable: false,
                DisplayName: string.Empty,
                Headline: null,
                University: null,
                FieldOfStudy: null,
                StudyYear: null,
                ShowHeadline: true,
                ShowUniversity: true,
                ShowFieldOfStudy: true,
                ShowStudyYear: true,
                OptedInAt: null,
                UpdatedAt: default,
                VisibleItemCount: visibleItemCount);
        }

        return new SearchableProfileResponse(
            IsSearchable: profile.IsSearchable,
            DisplayName: profile.DisplayName,
            Headline: profile.Headline,
            University: profile.University,
            FieldOfStudy: profile.FieldOfStudy,
            StudyYear: profile.StudyYear,
            ShowHeadline: profile.ShowHeadline,
            ShowUniversity: profile.ShowUniversity,
            ShowFieldOfStudy: profile.ShowFieldOfStudy,
            ShowStudyYear: profile.ShowStudyYear,
            OptedInAt: profile.OptedInAt,
            UpdatedAt: profile.UpdatedAt,
            VisibleItemCount: visibleItemCount);
    }

    /// <summary>
    /// Count of analyzed portfolio items with Strong or Developing findings
    /// for the student — the same set the refresh processor would copy
    /// into the index projection. Skips <c>Missing</c>-only items because
    /// the student can't claim a skill they were told was missing.
    /// </summary>
    private static async Task<int> CountQualifyingItemsAsync(
        WriteDbContext dbContext,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var analyzedItemIds = await dbContext.PortfolioItems
            .AsNoTracking()
            .Where(item => item.AccountId == accountId
                && item.AnalysisStatus == PortfolioAnalysisStatuses.Analyzed)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (analyzedItemIds.Count == 0)
        {
            return 0;
        }

        // Filter down to items that have at least one Strong/Developing
        // finding (Missing-only items contribute zero to the count and
        // are excluded from the projection).
        var qualifyingItemIds = await dbContext.PortfolioSkillFindings
            .AsNoTracking()
            .Where(f => analyzedItemIds.Contains(f.PortfolioItemId)
                && (f.ConfidenceBand == ConfidenceBands.Strong
                    || f.ConfidenceBand == ConfidenceBands.Developing))
            .Select(f => f.PortfolioItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return qualifyingItemIds.Count;
    }
}
