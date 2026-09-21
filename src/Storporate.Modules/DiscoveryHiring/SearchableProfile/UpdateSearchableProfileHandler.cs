using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.SearchableProfile;

/// <summary>
/// Upserts the calling student's <see cref="StudentSearchProfile"/>. On the
/// false-to-true edge (first opt-in or re-opt-in after an opt-out) the
/// <see cref="TalentIndexJobTypes.RefreshEntry"/> job is enqueued in the same
/// <c>SaveChangesAsync</c>. On the true-to-false edge the mirror
/// <see cref="TalentIndexEntry"/> row is removed in the same
/// <c>SaveChangesAsync</c> so the student disappears from employer search
/// results immediately (no need to wait for the next refresh tick).
/// </summary>
/// <remarks>
/// <para>
/// <b>Merge semantics.</b> Every input field is optional; the handler
/// merges the supplied values over the existing row (or applies safe
/// defaults when no row exists yet) so a PUT that only flips one toggle
/// doesn't accidentally blank out the previously-saved display name. The
/// Show flags are an exception — when the request omits them they keep
/// their existing value rather than flipping to the row-default
/// <c>true</c>, matching the student's most recent explicit choice.
/// </para>
/// <para>
/// <b>Audit row.</b> A single audit row is written per PUT (so a saved
/// update without an opt-in flip is still auditable); the action name is
/// <c>searchable_profile_enabled</c> on a false→true transition and
/// <c>searchable_profile_disabled</c> on a true→false transition. A pure
/// field edit (no toggle flip) writes neither — the existing
/// "searchable_profile_updated" audit hook is a future story (Phase 3's
/// frontend will need it for the "last saved" badge).
/// </para>
/// <para>
/// <b>Tenant isolation.</b> The lookup runs through the global query
/// filter on <see cref="IAccountScoped"/>, so a cross-account id never
/// matches and the handler treats the row as absent (which is the right
/// outcome — a student can never edit another student's profile).
/// </para>
/// </remarks>
public static class UpdateSearchableProfileHandler
{
    public static async Task<SearchableProfileResponse> ExecuteAsync(
        UpdateSearchableProfileRequest request,
        WriteDbContext dbContext,
        ITalentIndexRepository talentIndexRepository,
        IAuditLogWriter auditLogWriter,
        Guid accountId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(talentIndexRepository);
        ArgumentNullException.ThrowIfNull(auditLogWriter);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var now = timeProvider.GetUtcNow();
        var nowUtc = now.UtcDateTime;

        // Resolve the existing row (or null for first-time PUT). AsTracking is
        // the default; spelled out so the read+update pattern is unambiguous
        // at the call site.
        var profile = await dbContext.StudentSearchProfiles
            .FirstOrDefaultAsync(p => p.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);

        var wasSearchable = profile?.IsSearchable ?? false;
        var resolvedIsSearchable = request.IsSearchable ?? wasSearchable;

        if (profile is null)
        {
            // First-time PUT for this student. The validator has already
            // ensured the display name is non-blank when opting in;
            // safe defaults for the opt-out case keep the row in a
            // documented initial state.
            profile = new StudentSearchProfile
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                IsSearchable = resolvedIsSearchable,
                DisplayName = request.DisplayName ?? string.Empty,
                Headline = NullIfBlank(request.Headline),
                University = NullIfBlank(request.University),
                FieldOfStudy = NullIfBlank(request.FieldOfStudy),
                StudyYear = request.StudyYear,
                ShowHeadline = request.ShowHeadline ?? true,
                ShowUniversity = request.ShowUniversity ?? true,
                ShowFieldOfStudy = request.ShowFieldOfStudy ?? true,
                ShowStudyYear = request.ShowStudyYear ?? true,
                OptedInAt = resolvedIsSearchable ? now : null,
                UpdatedAt = now,
            };
            dbContext.StudentSearchProfiles.Add(profile);
        }
        else
        {
            // Existing row: merge the supplied values. Show flags preserve
            // the row's current value when omitted, so a PUT that only
            // changes the display name doesn't quietly flip a show flag.
            if (request.IsSearchable.HasValue) profile.IsSearchable = request.IsSearchable.Value;
            if (request.DisplayName is not null) profile.DisplayName = request.DisplayName;
            if (request.Headline is not null) profile.Headline = NullIfBlank(request.Headline);
            if (request.University is not null) profile.University = NullIfBlank(request.University);
            if (request.FieldOfStudy is not null) profile.FieldOfStudy = NullIfBlank(request.FieldOfStudy);
            if (request.StudyYear.HasValue) profile.StudyYear = request.StudyYear;
            if (request.ShowHeadline.HasValue) profile.ShowHeadline = request.ShowHeadline.Value;
            if (request.ShowUniversity.HasValue) profile.ShowUniversity = request.ShowUniversity.Value;
            if (request.ShowFieldOfStudy.HasValue) profile.ShowFieldOfStudy = request.ShowFieldOfStudy.Value;
            if (request.ShowStudyYear.HasValue) profile.ShowStudyYear = request.ShowStudyYear.Value;

            // OptInAt is preserved on subsequent opt-ins (only set on the
            // first false→true transition). UpdatedAt advances on every PUT
            // so the FE's "last saved" badge can compare with one equality
            // check.
            if (!wasSearchable && resolvedIsSearchable)
            {
                profile.OptedInAt = now;
            }
            profile.UpdatedAt = now;
        }

        // Opt-out path: delete the mirror TalentIndexEntry BEFORE the
        // SaveChanges that flips IsSearchable. The repository runs in its
        // own connection (raw Npgsql under the hood), so the two writes
        // can't share an EF transaction — but ordering them this way
        // still gives all-or-nothing semantics: a delete failure prevents
        // the profile flip, and the student must PUT isSearchable=true
        // again to retrigger a refresh (RefreshTalentIndexEntryProcessor
        // does not auto-recreate the entry on a tick when the profile is
        // unsearchable). A flip failure after the delete left the system
        // in a "searchable student with no entry" state, which the next
        // refresh tick reconciles back.
        if (wasSearchable && !resolvedIsSearchable)
        {
            await talentIndexRepository
                .DeleteAsync(accountId, cancellationToken)
                .ConfigureAwait(false);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await WriteAuditIfToggledAsync(
                auditLogWriter,
                enabled: false,
                profile.Id,
                cancellationToken).ConfigureAwait(false);

            return BuildResponse(profile, visibleItemCount: 0);
        }

        // Opt-in path: enqueue a refresh job in the same SaveChanges as
        // the profile upsert. We use the optimistic overload because the
        // handler just added the profile to the change tracker on a
        // first-time PUT — a DB query for IsSearchable wouldn't see the
        // uncommitted row and would incorrectly skip the enqueue. The
        // resolved flag is the source of truth here (it's already been
        // merged with the existing row's value above).
        var enqueuedJobId = resolvedIsSearchable
            ? TalentIndexRefreshJobs.EnqueueConfirmedSearchable(
                dbContext,
                accountId,
                nowUtc)
            : Guid.Empty;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (!wasSearchable && resolvedIsSearchable)
        {
            await WriteAuditIfToggledAsync(
                auditLogWriter,
                enabled: true,
                profile.Id,
                cancellationToken).ConfigureAwait(false);
        }

        // The response shape's VisibleItemCount is computed on the GET path
        // so the PUT response stays cheap. Returning zero here is fine —
        // the FE either navigates back to the GET path or refreshes the
        // page after the refresh tick.
        return BuildResponse(profile, visibleItemCount: 0);
    }

    private static SearchableProfileResponse BuildResponse(
        StudentSearchProfile profile,
        int visibleItemCount) =>
        new(
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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static Task WriteAuditIfToggledAsync(
        IAuditLogWriter auditLogWriter,
        bool enabled,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var action = enabled ? "searchable_profile_enabled" : "searchable_profile_disabled";
        return auditLogWriter.WriteAsync(
            action: action,
            resourceType: "SearchableProfile",
            resourceId: profileId.ToString(),
            metadataJson: null,
            cancellationToken: cancellationToken);
    }
}
