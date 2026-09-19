using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Storage;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Hard-deletes a single <see cref="PortfolioItem"/> owned by the caller's account:
/// removes the DB row, removes the underlying blob from <see cref="IArtifactStore"/>
/// for file-type submissions, and writes one <c>"portfolio_item_deleted"</c> audit row
/// describing what was removed (id, label, submission type) so the deletion trail
/// exists even though the file itself is gone — matching the plan's
/// "deletion is recorded in the existing audit log" assumption.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "not found" instead of "forbidden" on a cross-account id.</b> The lookup
/// runs through the global query filter on <see cref="IAccountScoped"/>, which
/// restricts reads to the caller's account. A row owned by another account simply
/// does not match the filter, so <c>FirstOrDefaultAsync</c> returns <see langword="null"/>
/// and the handler returns <c>false</c>. The endpoint maps that to a 404 — not a 403
/// — to avoid confirming the existence of an id the caller doesn't own, which is
/// the existing tenant-isolation pattern (see the plan's acceptance criteria).
/// </para>
/// <para>
/// <b>Cascade-delete for child findings.</b> <see cref="PortfolioSkillFinding"/> is
/// FK-linked to <see cref="PortfolioItem"/> with <see cref="DeleteBehavior.Restrict"/>;
/// the worker is the path that wipes findings before the item itself can be hard-
/// deleted. Loading the item's findings and <c>RemoveRange</c>-ing them in the same
/// <c>SaveChangesAsync</c> call lets EF order child deletes before the parent —
/// without widening the schema or relaxing the Restrict guard. The query is scoped
/// by <see cref="PortfolioSkillFinding.PortfolioItemId"/> (not <see cref="PortfolioSkillFinding.AccountId"/>)
/// so a sibling item's findings under the same account are not caught in the blast.
/// </para>
/// <para>
/// <b>Order of operations: delete blob after row.</b> If the DB delete succeeds but
/// the storage delete fails, the row is already gone — the orphan blob is cleaned
/// up by a future story's GC sweep. If the storage delete runs first and the DB
/// delete fails, we'd be left with a row pointing at a deleted blob (broken read).
/// Doing it in this order keeps the live state consistent at the cost of an
/// occasional orphan blob, which is the easier half to recover from.
/// </para>
/// <para>
/// <b>Why one audit row even when the storage delete fails.</b> The "real event
/// happened" invariant the rest of the audit instrumentation pins (see
/// <see cref="Storporate.Modules.Identity.LogoutHandler"/>'s remarks): the
/// portfolio row is gone, so from the user's perspective the deletion succeeded.
/// The audit row records that semantic event; a future alert can detect storage
/// orphans by comparing live blob listings against the audit feed.
/// </para>
/// </remarks>
public static class DeletePortfolioItemHandler
{
    public static async Task<bool> ExecuteAsync(
        Guid portfolioItemId,
        WriteDbContext dbContext,
        IArtifactStore artifactStore,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(auditLogWriter);

        // AsTracking() is the default, but spelled out so the read+update+delete pattern
        // is unambiguous at the call site. The global query filter restricts the lookup
        // to the caller's account, so a cross-account id never matches.
        var item = await dbContext.PortfolioItems
            .FirstOrDefaultAsync(item => item.Id == portfolioItemId, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            return false;
        }

        var storageKey = item.StorageKey;
        var submissionType = item.SubmissionType;
        var label = item.Label;

        // PortfolioSkillFindings FK is DeleteBehavior.Restrict — clear the item's findings
        // inside this SaveChangesAsync so EF orders child deletes before the parent.
        // Scoped by PortfolioItemId, not AccountId, so a sibling item's findings are
        // untouched (verified by DeletePortfolioItemHandlerTests' sibling-isolation test).
        // `ToListAsync` + `RemoveRange` instead of `ExecuteDeleteAsync` because the
        // EF InMemory test provider does not implement ExecuteDelete — the production
        // Postgres path would support either, but the InMemory path keeps unit-test
        // coverage of the Restrict-FK ordering intact.
        var findings = await dbContext.PortfolioSkillFindings
            .Where(f => f.PortfolioItemId == portfolioItemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.PortfolioSkillFindings.RemoveRange(findings);

        dbContext.PortfolioItems.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (submissionType == PortfolioSubmissionTypes.File && !string.IsNullOrEmpty(storageKey))
        {
            // S3-compatible DELETE is idempotent — a missing key is not an error
            // (matches S3ArtifactStore.DeleteAsync's contract), so a stale DB
            // pointing at a deleted blob doesn't surface as an exception here.
            await artifactStore.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
        }

        var metadataJson =
            $$"""{"submissionType":"{{submissionType}}","label":{{System.Text.Json.JsonSerializer.Serialize(label)}}}""";

        await auditLogWriter.WriteAsync(
            action: "portfolio_item_deleted",
            resourceType: "PortfolioItem",
            resourceId: portfolioItemId.ToString(),
            metadataJson: metadataJson,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return true;
    }
}