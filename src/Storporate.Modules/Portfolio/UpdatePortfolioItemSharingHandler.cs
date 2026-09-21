using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// STOR-44 Phase 1: flip the per-item
/// <see cref="PortfolioItem.ShareOriginalWithEmployers"/> flag for a single owned
/// item and, on a real change, propagate the consequence to the student's
/// non-tenant <see cref="TalentIndexEntry"/>:
/// <list type="bullet">
///   <item>Same value: no-op (still 200, no refresh job, no audit row).</item>
///   <item>false → true: persist the flag, enqueue a
///   <see cref="TalentIndexJobTypes.RefreshEntry"/> job in the same
///   <c>SaveChangesAsync</c> so the refresh processor writes the descriptor
///   into <see cref="TalentIndexEntry.ItemsJson"/> on the next tick.</item>
///   <item>true → false: FIRST call
///   <see cref="ITalentIndexRepository.ClearOriginalAsync"/> for that item
///   (which removes just the <c>original</c> descriptor, no re-embedding),
///   THEN write the flag and save. The flag flip and the descriptor removal
///   ride on the same <c>SaveChangesAsync</c> when possible, but the
///   ordering matters: a stale descriptor must never outlive the student's
///   decision, so the safe failure direction is "ClearOriginalAsync throws,
///   the flag stays true, the request fails".</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Switch-off ordering rationale.</b> The plan's "switch OFF" rule pins the
/// ordering as Clear → flag-flip → save. The reasoning is that the
/// <see cref="ITalentIndexRepository"/> runs in its own connection (raw Npgsql),
/// so the two writes can't share an EF transaction — but ordering them
/// Clear → save still gives the desired safety: if Clear fails the request
/// fails with the flag still true and the descriptor still intact, so the
/// student can retry. If Save fails after Clear, the descriptor is gone but
/// the flag still reads true; the next refresh tick re-reads the items, sees
/// the flag, sees no descriptor on disk (because the projection hasn't
/// rebuilt yet), and rewrites the entry without the descriptor. Either
/// failure direction reconciles on the next refresh tick without manual
/// intervention.
/// </para>
/// <para>
/// <b>Audit row.</b> A real change writes one <c>portfolio_sharing_changed</c>
/// audit row whose metadata carries the item id and the new flag value ONLY
/// — never the file name or URL, mirroring the privacy posture the
/// <see cref="DeletePortfolioItemHandler"/> audit row enforces. The no-op
/// same-value branch writes no audit row.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> The lookup runs through the global query filter on
/// <see cref="IAccountScoped"/>. A cross-account id never matches, so the
/// handler returns <see cref="UpdateOutcome.NotFound"/> and the endpoint maps
/// to 404 (not 403), mirroring how <see cref="DeletePortfolioItemHandler"/>
/// treats another account's portfolio id.
/// </para>
/// </remarks>
public static class UpdatePortfolioItemSharingHandler
{
    /// <summary>The shape returned to the endpoint. Endpoint maps
    /// <see cref="NotFound"/> to 404; everything else maps to 200.</summary>
    public sealed record UpdateOutcome(
        bool NotFound,
        PortfolioItemResponse? Response);

    public static async Task<UpdateOutcome> ExecuteAsync(
        Guid portfolioItemId,
        UpdatePortfolioItemSharingRequest request,
        WriteDbContext dbContext,
        ITalentIndexRepository talentIndexRepository,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(talentIndexRepository);
        ArgumentNullException.ThrowIfNull(auditLogWriter);

        // Validator has already enforced request.ShareOriginal != null —
        // a missing value would have surfaced as 400 share_original_required
        // before this handler ever runs.
        var newValue = request.ShareOriginal!.Value;

        // AsTracking is the default; spelled out so the read+update pattern
        // is unambiguous at the call site. The global query filter restricts
        // the lookup to the caller's account, so a cross-account id never
        // matches.
        var item = await dbContext.PortfolioItems
            .FirstOrDefaultAsync(i => i.Id == portfolioItemId, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            return new UpdateOutcome(NotFound: true, Response: null);
        }

        var previousValue = item.ShareOriginalWithEmployers;
        if (previousValue == newValue)
        {
            // No real change. Still 200 with the current state; no audit
            // row, no refresh job — matches the "no-op that still returns
            // 200" rule the plan calls out.
            return new UpdateOutcome(
                NotFound: false,
                Response: BuildResponse(item));
        }

        // Switch-OFF ordering: clear the descriptor from the search index
        // BEFORE the flag flip. If ClearOriginalAsync throws the request
        // fails, the flag stays true, and the student can retry — the safe
        // direction. The repository runs in its own connection so the clear
        // and the flag write are not in a single DB transaction, but the
        // ordering gives "no stale descriptor can outlive the flag".
        if (previousValue && !newValue)
        {
            await talentIndexRepository
                .ClearOriginalAsync(item.AccountId, item.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        item.ShareOriginalWithEmployers = newValue;

        // Switch-ON: enqueue a refresh job so the projection picks up the
        // descriptor on the next tick. EnqueueIfSearchableAsync short-
        // circuits when the student isn't opted in (no StudentSearchProfile
        // row OR IsSearchable == false), so a non-searchable student's
        // switch-on costs a single SELECT, not a job.
        await TalentIndexRefreshJobs
            .EnqueueIfSearchableAsync(
                dbContext,
                item.AccountId,
                nowUtc: DateTime.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var metadataJson =
            $$"""{"portfolioItemId":"{{item.Id}}","shareOriginal":{{(newValue ? "true" : "false")}}}""";

        await auditLogWriter.WriteAsync(
            action: "portfolio_sharing_changed",
            resourceType: "PortfolioItem",
            resourceId: item.Id.ToString(),
            metadataJson: metadataJson,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new UpdateOutcome(
            NotFound: false,
            Response: BuildResponse(item));
    }

    /// <summary>Build the wire response. Mirrors the projection used by
    /// <see cref="ListPortfolioItemsHandler"/> (label / category / skills
    /// previews would be re-derived for a list page; here we only need the
    /// single item shape the endpoint returns). StorageKey is intentionally
    /// not included — the student-facing UI never needs it.</summary>
    private static PortfolioItemResponse BuildResponse(PortfolioItem item) =>
        new(
            Id: item.Id,
            Label: item.Label,
            Category: item.Category,
            CustomCategoryText: item.CustomCategoryText,
            SubmissionType: item.SubmissionType,
            OriginalFileName: item.OriginalFileName,
            ContentType: item.ContentType,
            FileSizeBytes: item.FileSizeBytes,
            ExternalUrl: item.ExternalUrl,
            Description: item.Description,
            CreatedAt: item.CreatedAt,
            AnalysisStatus: item.AnalysisStatus,
            LastAnalyzedAt: item.LastAnalyzedAt,
            ShareOriginalWithEmployers: item.ShareOriginalWithEmployers,
            Skills: Array.Empty<PortfolioSkillPreview>());
}
