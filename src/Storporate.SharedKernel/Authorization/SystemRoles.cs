using Storporate.SharedKernel.Entities;

namespace Storporate.SharedKernel.Authorization;

/// <summary>
/// The platform's built-in roles and their permission grants. One role exists per
/// <see cref="ActorTypes"/> value, and a user's role is derived directly from their
/// <see cref="User.ActorType"/> — there is no per-user role assignment table and no
/// multi-role membership, by design (see the STOR-62 plan, Context &amp; Findings).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Grants"/> is the single mapping the authorization layer consults:
/// <c>SystemRoles.Grants[user.ActorType]</c> is the user's full permission set. <see cref="All"/>
/// above this class is intentionally used only for the <see cref="Administrator"/> grant set
/// (which is "everything"); the other four roles get narrower, domain-appropriate grants
/// reflecting who conventionally owns <see cref="Entities.Job"/>-related actions.
/// </para>
/// <para>
/// The grant sets defined here are first-cut defaults for STOR-62 Phase 2 — they capture the
/// shape of the role/permission model and prove the pipeline end-to-end against
/// <see cref="Permissions.Jobs"/> — rather than a fully specified business policy. Stories
/// that introduce new <see cref="Permissions"/> areas will extend these grants as part of
/// their own scope; the doc-comment on each role below records the current rationale so a
/// later reviewer can see what was assumed vs. what was specified.
/// </para>
/// </remarks>
public static class SystemRoles
{
    /// <summary>Self-service learner account — can apply to / browse jobs but does not post
    /// them.</summary>
    public static readonly string Student = ActorTypes.Student;

    /// <summary>Hiring-side account — posts and manages the <see cref="Entities.Job"/> rows
    /// they own.</summary>
    public static readonly string Organization = ActorTypes.Organization;

    /// <summary>Institution-side account — typically a posting or sponsorship source rather
    /// than a hiring party; read-only on <see cref="Entities.Job"/> for now.</summary>
    public static readonly string University = ActorTypes.University;

    /// <summary>Community / club account — read-only on <see cref="Entities.Job"/> plus
    /// <see cref="Permissions.ClubProfiles.Manage"/> for its own public profile (STOR-69).</summary>
    public static readonly string Club = ActorTypes.Club;

    /// <summary>Platform-staff role. Deliberately bypasses workspace isolation (see
    /// <see cref="ActorTypes"/> class remarks) and gets the full permission set so it can
    /// read / write every account's data for support and moderation.</summary>
    public static readonly string Administrator = ActorTypes.Administrator;

    /// <summary>
    /// Human-readable descriptions of each role. Used by diagnostics / future admin tooling
    /// rather than for authorization decisions.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [ActorTypes.Student] = "Self-service learner account — applies to and browses jobs.",
        [ActorTypes.Organization] = "Hiring-side account — posts and manages job listings.",
        [ActorTypes.University] = "Institution-side account — sponsors / surfaces opportunities.",
        [ActorTypes.Club] = "Community / club account — engages with the platform's discovery surfaces.",
        [ActorTypes.Administrator] = "Platform staff — bypasses workspace isolation by design.",
    };

    /// <summary>
    /// Permissions that the <see cref="Administrator"/> role's grant set
    /// does NOT include, even though <see cref="Permissions.All"/> does
    /// contain them. The carve-out is hand-maintained because the
    /// "Administrator gets everything" default is the simpler model for
    /// most permissions but a few deliberately-narrowed surfaces need an
    /// explicit exemption. STOR-44 Phase 2 ships
    /// <see cref="Permissions.CandidateReview.Read"/> on this list: the
    /// drill-down surface reads another student's
    /// <c>TalentIndexEntry</c>, and even Administrator's
    /// workspace-isolation bypass does not extend to opening an
    /// employer-facing drill-down view on someone else's data. A future
    /// story that wants to open any of these for Administrator widens
    /// both this list AND the matching test in one PR.
    /// </summary>
    /// <remarks>
    /// Declared BEFORE <see cref="Grants"/> because C# static field
    /// initializers run in textual source order and <see cref="BuildGrants"/>
    /// enumerates this set when computing the Administrator entry.
    /// </remarks>
    public static readonly IReadOnlySet<string> AdministratorExcludedFromAll = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.CandidateReview.Read,
    };

    /// <summary>
    /// Maps each <see cref="SystemRoles"/> role name to its full permission set. The
    /// <see cref="Administrator"/> entry starts as <see cref="Permissions.All"/> by
    /// construction, then has <see cref="AdministratorExcludedFromAll"/>
    /// carved out; the other four roles get narrower, domain-appropriate
    /// defaults documented on the role constants above.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Grants = BuildGrants();

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildGrants()
    {
        // First-cut defaults, not a finalized business policy. Reasoning:
        //   - Organization is the hiring party on this domain: it posts and manages Job rows
        //     it owns, so it gets the full Jobs.* pair (Read + Write).
        //   - Student / University / Club are not (currently) hiring parties: they consume
        //     Job listings, so they get Jobs.Read only. A future story that introduces a
        //     write-side workflow for any of these (e.g. a Club posting its own
        //     opportunities, a University sponsoring one) widens its grant set then.
        //   - Administrator gets Permissions.All by default — full access
        //     across every account, enforced by the workspace-isolation
        //     bypass path (see ActorTypes remarks). The
        //     <see cref="AdministratorExcludedFromAll"/> set is the single
        //     hand-maintained carve-out list: any constant listed there is
        //     deliberately NOT granted to the Administrator role. Today
        //     that is just CandidateReview.Read — STOR-44 Phase 2 ships
        //     the new "employer drill-down" surface and the spec
        //     deliberately narrows Administrator's reach over OTHER
        //     students' TalentIndexEntry rows so a future support path
        //     has to make a deliberate, reviewable change before an
        //     Administrator can open somebody's drill-down.
        //   - STOR-37 Phase 1: Students are the only actors who use the Portfolio
        //     surface, so the Student grant set is widened to include the full
        //     Portfolio.* triple (Create + Read + Delete). University / Club /
        //     Organization have no use for portfolio capture, so they keep their existing
        //     Jobs.Read-only grant set unchanged.
        //   - STOR-43 Phase 1: Organizations gain TalentSearch.* (Create + Read) so
        //     Phase 2 can ship the employer search endpoints; Students gain
        //     SearchableProfile.* (Read + Update) so the opt-in endpoints can
        //     gate on permissions like every other module's surface. Neither role
        //     gets the other's grant — the talent-search surface is hiring-side and
        //     the searchable-profile surface is student-side.
        //   - STOR-44 Phase 2: Organizations gain CandidateReview.Read so the
        //     employer drill-down endpoints (GET review + GET original) can
        //     gate on a permission like every other module's surface. Students
        //     get nothing here because the surface reads OTHER students'
        //     TalentIndexEntry rows, not their own.
        var readOnly = new HashSet<string>(StringComparer.Ordinal) { Permissions.Jobs.Read };
        var fullJobs = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.Jobs.Read,
            Permissions.Jobs.Write,
            // STOR-43 Phase 1: Organization is the only hiring-side actor and the
            // talent-search pool is the Organization-only view of opted-in students.
            Permissions.TalentSearch.Create,
            Permissions.TalentSearch.Read,
            // STOR-44 Phase 2: Organization is the only actor that drills
            // down behind a search result into a single student's
            // TalentIndexEntry — the GET review and original endpoints share
            // one Read permission. The Student keeps nothing here because
            // the surface is the employer-facing view of OTHER students'
            // (opted-in) entries.
            Permissions.CandidateReview.Read,
            // STOR-66: Organizations publish and manage their own job / internship postings.
            Permissions.JobPostings.Manage,
            // STOR-67: Organizations review applicants to their own postings.
            Permissions.JobApplications.Review,
            // STOR-68: Organizations shortlist candidates and message them.
            Permissions.Outreach.Send,
            // STOR-69: Organizations browse Published club profiles.
            Permissions.ClubProfiles.Read,
            // STOR-70: Organizations record their sponsorship goals.
            Permissions.SponsorshipGoals.Manage,
            // STOR-71: Organizations see clubs that fit their goal sets.
            Permissions.SponsorshipMatching.Read,
        };
        var studentGrants = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.Jobs.Read,
            Permissions.Portfolio.Create,
            Permissions.Portfolio.Read,
            Permissions.Portfolio.Delete,
            Permissions.Portfolio.Retry,
            // STOR-44 Phase 1: students are the only actors who toggle the
            // per-item employer drill-down switch (PUT /api/portfolio/items/{id}/sharing);
            // the Update verb exists specifically for that endpoint, so it lives
            // exclusively in the Student grant set. Organizations and
            // Administrators have no use for a per-item sharing control —
            // the index entry they read in Phase 2 is a derived snapshot, not
            // a row they mutate.
            Permissions.Portfolio.Update,
            // STOR-40 Phase 1: the Student is the only actor type that uses the
            // advisor and feed surfaces, so the Student grant set widens here.
            // Advisor: full Create/Read/Update/Delete so the student can drive
            // the conversation lifecycle end-to-end (open an Exploration,
            // message, refresh, rename, delete, compare). Feed: Read so the
            // student sees matched items + Update for the future
            // "mark-as-read" / "dismiss" UI. Organization / University /
            // Club are unchanged — the advisor pipeline is a student-only
            // feature today.
            Permissions.Advisor.Create,
            Permissions.Advisor.Read,
            Permissions.Advisor.Update,
            Permissions.Advisor.Delete,
            Permissions.Feed.Read,
            Permissions.Feed.Update,
            // STOR-43 Phase 1: only the Student owns a SearchableProfile row,
            // and only they can opt in or out. Read powers the GET endpoint;
            // Update powers the PUT endpoint.
            Permissions.SearchableProfile.Read,
            Permissions.SearchableProfile.Update,
            // STOR-66: Students browse Open postings and see their skill fit.
            Permissions.JobPostings.Read,
            // STOR-67: Students apply to postings and follow their applications.
            Permissions.JobApplications.Apply,
            // STOR-68: Students read and answer invitations from employers.
            Permissions.Outreach.Respond,
        };
        // STOR-69: Club keeps Jobs.Read and gains the manage side of its own public profile.
        // Kept as its own set (not the shared readOnly set) so University is unaffected.
        var clubGrants = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.Jobs.Read,
            Permissions.ClubProfiles.Manage,
            // STOR-70: Clubs read the Active sponsorship goal sets of companies.
            Permissions.SponsorshipGoals.Read,
            // STOR-71: Clubs see companies whose goals fit their profile.
            Permissions.SponsorshipMatching.Read,
        };
        var administrator = new HashSet<string>(Permissions.All, StringComparer.Ordinal);
        // The drill-down surface reads another student's TalentIndexEntry;
        // even Administrator's workspace-isolation bypass does not extend
        // to opening an employer-facing drill-down view on someone
        // else's data — a deliberate carve-out to keep the permission
        // gating tight. Drop the constant from the Administrator grant
        // set explicitly; the PermissionsTests "All = Administrator"
        // assertion is updated to the same carve-out. A future story
        // opening any of these for Administrator widens both this list
        // AND the matching test in one PR.
        foreach (var excluded in AdministratorExcludedFromAll)
        {
            administrator.Remove(excluded);
        }

        // Dictionary keys reference ActorTypes.* rather than the role-name string literals
        // declared on this class so a typo in either side is caught by the compiler instead
        // of silently producing a never-looked-up grant set.
        return new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ActorTypes.Student] = studentGrants,
            [ActorTypes.Organization] = fullJobs,
            [ActorTypes.University] = readOnly,
            [ActorTypes.Club] = clubGrants,
            [ActorTypes.Administrator] = administrator,
        };
    }
}
