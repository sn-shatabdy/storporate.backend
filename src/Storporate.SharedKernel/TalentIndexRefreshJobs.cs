using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.SharedKernel.Entities;

namespace Storporate.SharedKernel;

/// <summary>
/// Enqueue helper for the <see cref="TalentIndexJobTypes.RefreshEntry"/> job.
/// The producer-side twin of <c>RefreshTalentIndexEntryProcessor</c>: three
/// call sites (<c>PortfolioAnalysisJobProcessor</c> on a successful analysis,
/// <c>DeletePortfolioItemHandler</c> on a portfolio-item removal, and the
/// <c>UpdateSearchableProfileHandler</c> on a false-to-true toggle) each call
/// <see cref="EnqueueIfSearchableAsync"/> with the affected student account id
/// and the change tracker flushes them in a single <c>SaveChangesAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a SharedKernel helper, not a module-internal one.</b>
/// The Portfolio module is the most common producer (it enqueues a refresh
/// after every successful analysis and every deletion) and the module-
/// isolation rule (<c>ModuleIsolationTests.Module_ShouldNotDependOn_SiblingModule</c>)
/// forbids it from referencing the DiscoveryHiring module. The helper
/// therefore lives next to <see cref="TalentIndexJobTypes"/> in SharedKernel
/// alongside the discriminator string and the payload record. The companion
/// processor still lives in the DiscoveryHiring module where it belongs.
/// </para>
/// <para>
/// <b>Why the helper takes a <see cref="DbContext"/>, not the typed
/// <c>WriteDbContext</c>.</b>
/// SharedKernel already references EF Core but does NOT reference
/// <c>Storporate.Infrastructure</c> (intentional — Infrastructure depends on
/// SharedKernel, never the other way). <see cref="DbContext.Set{TEntity}()"/>
/// is the cross-tenant way to access entity sets without taking a dependency
/// on the typed context. The global query filter on
/// <see cref="IAccountScoped"/> applies to <see cref="StudentSearchProfile"/>
/// regardless of how the set was resolved, so the cross-account guard still
/// holds.
/// </para>
/// <para>
/// <b>Why the helper short-circuits when the student isn't searchable.</b>
/// Refresh work is only meaningful for opted-in students — the processor
/// would otherwise pull a profile row, see <c>IsSearchable = false</c>, and
/// delete any prior entry. Filtering the enqueue up-front keeps the queue
/// shallow and makes the "no entry exists" path the default rather than a
/// fall-through. The handler enqueues under <see cref="IAccountScoped"/>
/// account scope so a cross-account lookup returns null and the enqueue is
/// skipped — same tenant-isolation guarantee as everywhere else.
/// </para>
/// <para>
/// <b>Why the helper does NOT call <c>SaveChangesAsync</c>.</b>
/// Every caller already has a <c>SaveChanges</c> pending (status flips, row
/// inserts, deletes). Letting the helper commit independently would split a
/// logically atomic update into two commits and reintroduce the
/// "portfolio row updated but no refresh job enqueued" gap the original
/// portfolio / advisor enqueue paths already locked down. The matching
/// <c>SaveChangesAsync</c> happens at the call site.
/// </para>
/// <para>
/// <b>Why the helper resolves <see cref="StudentSearchProfile"/> instead of
/// trusting a boolean flag passed in.</b>
/// The most common call site is the
/// <c>PortfolioAnalysisJobProcessor</c>, which doesn't load the
/// <see cref="StudentSearchProfile"/> for any other reason. A boolean flag
/// would force every caller to either add a profile load or skip the
/// enqueue, neither of which is desirable. The helper does one
/// <c>AsNoTracking</c> read per call — cheap (indexed by <c>AccountId</c>) and
/// already covered by the global query filter.
/// </para>
/// </remarks>
public static class TalentIndexRefreshJobs
{
    /// <summary>
    /// Enqueue a <see cref="TalentIndexJobTypes.RefreshEntry"/> job for the
    /// given student, but only when the student's
    /// <see cref="StudentSearchProfile.IsSearchable"/> is true. The new job
    /// row is added to the supplied <see cref="DbContext"/>'s change tracker;
    /// the caller is responsible for the <c>SaveChangesAsync</c> that commits
    /// it atomically alongside whatever other write the call site was already
    /// making.
    /// </summary>
    /// <param name="dbContext">The caller's write context. The new job row is
    /// added to its change tracker.</param>
    /// <param name="studentAccountId">The owning student's
    /// <see cref="User.Id"/>.</param>
    /// <param name="nowUtc">The UTC timestamp the new job's
    /// <c>CreatedAt</c> / <c>UpdatedAt</c> are set to. The caller supplies
    /// this so multiple enqueues in the same request share one timestamp.</param>
    /// <returns>The id of the new job row, or <see cref="Guid.Empty"/> when
    /// the student is not (or no longer) searchable.</returns>
    public static async Task<Guid> EnqueueIfSearchableAsync(
        DbContext dbContext,
        Guid studentAccountId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        if (studentAccountId == Guid.Empty)
        {
            return Guid.Empty;
        }

        // AsNoTracking + lightweight projection: only IsSearchable is needed
        // for the gate, and the read runs through the IAccountScoped global
        // query filter so a cross-account id never matches. Returns false
        // when the row is absent (the student has never toggled the feature)
        // — same outcome as IsSearchable = false for the gate's purposes.
        var isSearchable = await dbContext.Set<StudentSearchProfile>()
            .Where(p => p.AccountId == studentAccountId)
            .Select(p => (bool?)p.IsSearchable)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? false;

        if (!isSearchable)
        {
            return Guid.Empty;
        }

        return EnqueueJobCore(dbContext, studentAccountId, nowUtc);
    }

    /// <summary>
    /// Enqueue when the caller already knows the student's resolved
    /// <c>IsSearchable</c> — the optimistic variant <see cref="EnqueueIfSearchableAsync"/>
    /// can't satisfy because the helper queries the DB and a profile row that
    /// was just added in the same change tracker hasn't committed yet. The
    /// <see cref="UpdateSearchableProfileHandler"/> uses this for the
    /// first-time-PUT path so the new profile's <c>IsSearchable = true</c>
    /// immediately produces a refresh job in the same SaveChanges.
    /// </summary>
    public static Guid EnqueueConfirmedSearchable(
        DbContext dbContext,
        Guid studentAccountId,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        if (studentAccountId == Guid.Empty)
        {
            return Guid.Empty;
        }
        return EnqueueJobCore(dbContext, studentAccountId, nowUtc);
    }

    private static Guid EnqueueJobCore(DbContext dbContext, Guid studentAccountId, DateTime nowUtc)
    {
        var newJob = new Job
        {
            Id = Guid.NewGuid(),
            Type = TalentIndexJobTypes.RefreshEntry,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            PayloadJson = JsonSerializer.Serialize(
                new RefreshTalentIndexPayload(studentAccountId)),
            AccountId = studentAccountId,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
        dbContext.Set<Job>().Add(newJob);

        return newJob.Id;
    }
}
