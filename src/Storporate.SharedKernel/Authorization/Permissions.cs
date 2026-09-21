using System.Reflection;

namespace Storporate.SharedKernel.Authorization;

/// <summary>
/// Single source of truth for every permission string used by the platform's authorization layer.
/// Permission literals are organized into nested static classes, one per domain area, so each
/// constant has a stable, self-documenting call site (e.g. <c>Permissions.Jobs.Read</c>) and
/// adding a new permission is a one-line, type-checked change.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the established reflection-over-nested-types pattern (also seen in the workspace's
/// own <see cref="JobStatus"/> / <see cref="VerificationStatuses"/> / <see cref="Entities.ActorTypes"/>
/// siblings): <see cref="All"/> is materialized once from every public <c>const string</c> on
/// the nested classes, deduped and ordinal-sorted, so <see cref="SystemRoles"/> can use it as
/// the canonical "everything" set (Administrator's grant set) without a hand-maintained list
/// to drift out of sync with the actual constants.
/// </para>
/// <para>
/// STOR-62 Phase 2 deliberately only defines permission areas for the entities that exist in
/// the model today (<see cref="Entities.Job"/>). Future domains (Evidence Engine credentials,
/// etc.) gain their own nested class the same way when those stories ship — they are
/// <em>not</em> pre-invented here.
/// </para>
/// </remarks>
public static class Permissions
{
    /// <summary>
    /// Permissions governing the <see cref="Entities.Job"/> resource — the only
    /// <see cref="Entities.IAccountScoped"/> entity in the model as of STOR-62 Phase 2 and the
    /// first real-data entity to prove out the three-layer isolation pipeline (global query
    /// filter, save-time interceptor, PostgreSQL row-level security policy).
    /// </summary>
    public static class Jobs
    {
        /// <summary>Read any <see cref="Entities.Job"/> owned by the caller's account.</summary>
        public const string Read = "jobs:read";

        /// <summary>Create a new <see cref="Entities.Job"/> under the caller's account.</summary>
        public const string Write = "jobs:write";
    }

    /// <summary>
    /// Permissions governing the SecurityGovernance module's own surfaces — the audit log
    /// query endpoint and any future compliance / forensics / integrity-verification
    /// tools that live in the same module. Adding a constant here automatically grants
    /// it to the <see cref="SystemRoles.Administrator"/> role through the reflection-built
    /// <see cref="All"/> set; no <c>SystemRoles.cs</c> edit is required.
    /// </summary>
    /// <remarks>
    /// STOR-63 Phase 3: <see cref="ViewAuditLog"/> gates the
    /// <c>GET /api/security-governance/audit-log</c> endpoint. The audit table is not
    /// tenant-scoped (<see cref="Entities.AuditLogEntry"/> deliberately does not
    /// implement <see cref="Entities.IAccountScoped"/>), so this permission — not the
    /// global query filter — is what enforces "only Administrators can read it."
    /// </remarks>
    public static class SecurityGovernance
    {
        /// <summary>Read the platform-wide <see cref="Entities.AuditLogEntry"/> feed via
        /// the Administrator-only query endpoint. Held exclusively by
        /// <see cref="SystemRoles.Administrator"/> through <see cref="All"/>.</summary>
        public const string ViewAuditLog = "security_governance:view_audit_log";
    }

    /// <summary>
    /// Permissions governing the Portfolio module — student-facing capture,
    /// list, and hard-delete of <see cref="Entities.PortfolioItem"/> rows. Adding
    /// a constant here automatically grants it to
    /// <see cref="SystemRoles.Administrator"/> through the reflection-built
    /// <see cref="All"/> set; the explicit Student role grants are recorded in
    /// <see cref="SystemRoles"/> because the Student flow is the first
    /// non-Administrator-only use of the Portfolio surface.
    /// </summary>
    /// <remarks>
    /// STOR-37 Phase 1: <see cref="Create"/> gates <c>POST /api/portfolio/items</c>,
    /// <see cref="Read"/> gates <c>GET /api/portfolio/items</c>, and
    /// <see cref="Delete"/> gates <c>DELETE /api/portfolio/items/{id}</c>.
    /// The set is split per verb (rather than a single coarse "manage" permission) so a
    /// future role narrowing — e.g. read-only parent / mentor personas viewing a student's
    /// portfolio — can grant <see cref="Read"/> without granting write/delete powers.
    /// </remarks>
    public static class Portfolio
    {
        /// <summary>Create a new <see cref="Entities.PortfolioItem"/> under the caller's
        /// account (file upload or external link).</summary>
        public const string Create = "portfolio:create";

        /// <summary>Read <see cref="Entities.PortfolioItem"/> rows owned by the caller's
        /// account.</summary>
        public const string Read = "portfolio:read";

        /// <summary>Delete a <see cref="Entities.PortfolioItem"/> owned by the caller's
        /// account (hard-delete: DB row and storage blob).</summary>
        public const string Delete = "portfolio:delete";

        /// <summary>Re-queue analysis for a <see cref="Entities.PortfolioItem"/> whose
        /// <c>AnalysisStatus</c> is <c>Failed</c> (STOR-38 Phase 3 endpoint
        /// <c>POST /api/portfolio/items/{id}/analysis/retry</c>). Split from <see cref="Read"/>
        /// so a future read-only mentor persona can read a student's portfolio without
        /// gaining the ability to nudge the worker on the student's behalf.</summary>
        public const string Retry = "portfolio:retry";

        /// <summary>STOR-44 Phase 1: update the per-item metadata of an existing
        /// <see cref="Entities.PortfolioItem"/> owned by the caller's account — specifically
        /// the drill-down <c>ShareOriginalWithEmployers</c> flag exposed via
        /// <c>PUT /api/portfolio/items/{id}/sharing</c>. Split from <see cref="Read"/>
        /// so a future read-only mentor persona can read a student's portfolio without
        /// gaining the ability to flip the employer-visible sharing setting on the student's
        /// behalf. Held by <see cref="SystemRoles.Student"/> only; Organizations and
        /// Administrators have no use for the per-item employer-sharing surface.</summary>
        public const string Update = "portfolio:update";
    }

    /// <summary>
    /// Permissions governing the StudentGrowthExperience advisor surface
    /// (Explorations, messages, summaries, context notes, comparisons).
    /// STOR-40 Phase 1: the constants exist now so the per-resource
    /// <c>RequirePermission</c> gates can attach when the Phase 2 endpoints
    /// land; the endpoints themselves are not wired in this story.
    /// </summary>
    /// <remarks>
    /// The verb split mirrors <see cref="Portfolio"/>'s shape (Create /
    /// Read / Update / Delete) so a future role narrowing — e.g. a read-only
    /// parent persona reviewing a student's saved summaries — can grant
    /// <see cref="Read"/> without gaining write powers. The four-verb split
    /// is intentional even where Phase 2 doesn't yet exercise Update / Delete
    /// on every entity; the permission table is the contract that future
    /// stories will code against, and adding a verb later would be a breaking
    /// schema change for any role that already enumerates them.
    /// </remarks>
    public static class Advisor
    {
        /// <summary>Create a new <see cref="Entities.Exploration"/> (opening turn) or
        /// append a <see cref="Entities.ExplorationMessage"/> (subsequent turn) under
        /// the caller's account.</summary>
        public const string Create = "advisor:create";

        /// <summary>Read <see cref="Entities.Exploration"/>, <see cref="Entities.ExplorationMessage"/>,
        /// <see cref="Entities.ExplorationSummaryVersion"/>, <see cref="Entities.StudentContextNote"/>,
        /// and <see cref="Entities.ExplorationComparison"/> rows owned by the caller's account.</summary>
        public const string Read = "advisor:read";

        /// <summary>Update <see cref="Entities.Exploration"/> (e.g. <c>PUT /explorations/{id}/title</c>)
        /// and <see cref="Entities.ExplorationComparison"/> rows owned by the caller's account.</summary>
        public const string Update = "advisor:update";

        /// <summary>Delete <see cref="Entities.Exploration"/> and dependent rows
        /// (messages, summary versions, context notes, comparisons) under the
        /// caller's account.</summary>
        public const string Delete = "advisor:delete";
    }

    /// <summary>
    /// Permissions governing the StudentGrowthExperience feed surface
    /// (<see cref="Entities.StudentFeedEntry"/> rows that say "this outside
    /// <see cref="Entities.FeedItem"/> is relevant to this student"). STOR-40
    /// Phase 3 introduces the feed-refresh background service and the per-account
    /// match job; the constants exist now so the per-endpoint
    /// <c>RequirePermission</c> gates can attach when those endpoints land.
    /// </summary>
    /// <remarks>
    /// Note the absence of <c>Create</c> / <c>Delete</c> verbs — students never
    /// write or remove <see cref="Entities.StudentFeedEntry"/> rows directly. The
    /// match job (background, server-driven) writes them; the feed page reads
    /// them; the student's "mark as read" / "dismiss" UI is the only mutation
    /// the user sees, and that mutation lands under <see cref="Update"/>.
    /// </remarks>
    public static class Feed
    {
        /// <summary>Read <see cref="Entities.StudentFeedEntry"/> rows owned by the
        /// caller's account.</summary>
        public const string Read = "feed:read";

        /// <summary>Update <see cref="Entities.StudentFeedEntry"/> rows owned by the
        /// caller's account (the future "mark as read" / "dismiss" UI).</summary>
        public const string Update = "feed:update";
    }

    /// <summary>
    /// Permissions governing the employer talent-search surface (STOR-43). STOR-43
    /// Phase 1 introduces the student opt-in profile and the non-tenant search
    /// index that an Organization will eventually query; the constants exist now so
    /// the per-endpoint <c>RequirePermission</c> gates can attach when the student
    /// opt-in endpoints land. Phase 2 adds the search endpoints themselves
    /// (<c>POST /api/discovery/talent-searches</c>,
    /// <c>GET /api/discovery/talent-searches/{id}</c>) and reuses these same constants.
    /// </summary>
    /// <remarks>
    /// Split mirrors the <see cref="Advisor"/> verb split — Create + Read — so a
    /// future role narrowing (e.g. an Organization persona that can submit searches
    /// but not read prior results) can grant <see cref="Create"/> without granting
    /// <see cref="Read"/>. Held by <see cref="SystemRoles.Organization"/> only;
    /// Students / Universities / Clubs get nothing here because the surface is
    /// hiring-side.
    /// </remarks>
    public static class TalentSearch
    {
        /// <summary>Submit a new employer talent search against the
        /// <see cref="Entities.TalentIndexEntry"/> pool. Phase 1 only declares the
        /// constant; Phase 2 wires the <c>POST /api/discovery/talent-searches</c>
        /// endpoint that uses it.</summary>
        public const string Create = "talent-search:create";

        /// <summary>Read the result of a previously-submitted talent search by id.
        /// Phase 1 only declares the constant; Phase 2 wires the
        /// <c>GET /api/discovery/talent-searches/{id}</c> endpoint that uses it.</summary>
        public const string Read = "talent-search:read";
    }

    /// <summary>
    /// Permissions governing the employer drill-down into a single student
    /// surfaced by a STOR-43 talent search (STOR-44 Phase 2). STOR-43
    /// Phase 1 introduces the student opt-in profile and the non-tenant
    /// search index that an Organization will eventually query; STOR-44
    /// Phase 2 adds the two endpoints that read ONE entry from that
    /// index (<c>GET /api/discovery/candidates/{candidateId}</c> and
    /// <c>GET /api/discovery/candidates/{candidateId}/items/{portfolioItemId}/original</c>).
    /// </summary>
    /// <remarks>
    /// Held by <see cref="SystemRoles.Organization"/> only; Students / Universities /
    /// Clubs never read the multi-student drill-down surface because the
    /// endpoints are the employer-facing "open one candidate" view, and
    /// Administrator gets it implicitly through <see cref="All"/> for
    /// future support tooling. Both endpoints share the same single
    /// <see cref="Read"/> permission: a future split (e.g. gating the
    /// original-file stream separately from the review summary) would
    /// add a new constant here without disturbing the existing one.
    /// </remarks>
    public static class CandidateReview
    {
        /// <summary>Read the single-student review summary and stream the
        /// per-item original file or external link behind the same gate.
        /// Used by <c>GET /api/discovery/candidates/{candidateId}</c> and
        /// <c>GET /api/discovery/candidates/{candidateId}/items/{portfolioItemId}/original</c>.</summary>
        public const string Read = "candidate-review:read";
    }

    /// <summary>
    /// Permissions governing the student opt-in "let employers find me" surface
    /// (STOR-43 Phase 1). The constants gate
    /// <c>GET /api/discovery/searchable-profile</c> and
    /// <c>PUT /api/discovery/searchable-profile</c>; only the owning Student ever
    /// reads or writes their own <see cref="Entities.StudentSearchProfile"/> row
    /// (the EF global query filter + RLS policy both key on
    /// <see cref="Entities.IAccountScoped.AccountId"/>).
    /// </summary>
    /// <remarks>
    /// Held by <see cref="SystemRoles.Student"/> only; Organizations / Universities
    /// / Clubs / Administrator get nothing here because the surface is the
    /// student's own opt-in profile (the Administrator role covers it through
    /// <see cref="All"/> if a future story ever needs a support path).
    /// </remarks>
    public static class SearchableProfile
    {
        /// <summary>Read the caller's own <see cref="Entities.StudentSearchProfile"/>
        /// (used by <c>GET /api/discovery/searchable-profile</c>).</summary>
        public const string Read = "searchable-profile:read";

        /// <summary>Update the caller's own <see cref="Entities.StudentSearchProfile"/>
        /// (used by <c>PUT /api/discovery/searchable-profile</c>).</summary>
        public const string Update = "searchable-profile:update";
    }

    /// <summary>
    /// Permissions governing job and internship postings (STOR-66). <see cref="Manage"/> is the
    /// Organization-side publish / edit / pause / reopen / close surface; <see cref="Read"/> is the
    /// Student-side browse surface over Open postings. Administrator receives both via
    /// <see cref="All"/>.
    /// </summary>
    public static class JobPostings
    {
        /// <summary>Create, edit, list and change the status of the caller's own postings.</summary>
        public const string Manage = "job-postings:manage";

        /// <summary>Browse Open postings and see how well each fits the caller's skills.</summary>
        public const string Read = "job-postings:read";
    }

    /// <summary>
    /// Permissions governing student applications to postings (STOR-67). <see cref="Apply"/> is
    /// Student-only (apply and list own applications); <see cref="Review"/> is Organization-only
    /// (read applicants of own postings and shortlist / decline them). Administrator receives both
    /// via <see cref="All"/>.
    /// </summary>
    public static class JobApplications
    {
        /// <summary>Apply to an Open posting and list the caller's own applications.</summary>
        public const string Apply = "job-applications:apply";

        /// <summary>Read applicants of the caller's own postings and set their status.</summary>
        public const string Review = "job-applications:review";
    }

    /// <summary>
    /// Permissions governing employer-student outreach (STOR-68). <see cref="Send"/> is
    /// Organization-only (shortlist candidates, invite them, message, read their side);
    /// <see cref="Respond"/> is Student-only (inbox, reply, decline). Administrator receives both
    /// via <see cref="All"/>.
    /// </summary>
    public static class Outreach
    {
        /// <summary>Shortlist candidates, send invitations and messages, and read the caller's own conversations.</summary>
        public const string Send = "outreach:send";

        /// <summary>Read the caller's inbox, reply to an invitation and decline it.</summary>
        public const string Respond = "outreach:respond";
    }

    /// <summary>
    /// Permissions governing club profiles (STOR-69). <see cref="Manage"/> is Club-only (build, edit,
    /// publish and unpublish the caller's own profile); <see cref="Read"/> is Organization-only
    /// (browse Published club profiles). Administrator receives both via <see cref="All"/>.
    /// </summary>
    public static class ClubProfiles
    {
        /// <summary>Create, edit, publish and unpublish the caller's own club profile.</summary>
        public const string Manage = "club-profile:manage";

        /// <summary>Browse Published club profiles.</summary>
        public const string Read = "club-profile:read";
    }

    /// <summary>
    /// Permissions governing company sponsorship goals (STOR-70). <see cref="Manage"/> is
    /// Organization-only (create, edit, pause and delete the caller's own goal sets);
    /// <see cref="Read"/> is Club-only (read Active goal sets). Administrator receives both via
    /// <see cref="All"/>.
    /// </summary>
    public static class SponsorshipGoals
    {
        /// <summary>Create, edit, pause and delete the caller's own sponsorship goal sets.</summary>
        public const string Manage = "sponsorship-goals:manage";

        /// <summary>Read Active company sponsorship goal sets.</summary>
        public const string Read = "sponsorship-goals:read";
    }

    /// <summary>
    /// Permission governing sponsor-club matching (STOR-71). <see cref="Read"/> is granted to both
    /// Organization (clubs that fit one of its goal sets) and Club (companies that fit its profile);
    /// Administrator receives it via <see cref="All"/>.
    /// </summary>
    public static class SponsorshipMatching
    {
        /// <summary>Read fit suggestions and their plain-language reasons.</summary>
        public const string Read = "sponsorship-matches:read";
    }

    /// <summary>
    /// Every permission literal defined across the nested classes above, deduped and
    /// ordinal-sorted. Built once via reflection at class-initialization time so adding a new
    /// nested class / const is automatically reflected here without a hand-edited list.
    /// </summary>
    public static readonly IReadOnlyList<string> All = BuildAll();

    private static IReadOnlyList<string> BuildAll()
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var nestedType in typeof(Permissions).GetNestedTypes(BindingFlags.Public | BindingFlags.Static))
        {
            foreach (var field in nestedType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (!field.IsLiteral || field.FieldType != typeof(string) || field.GetRawConstantValue() is not string value)
                {
                    continue;
                }

                // SortedSet.Add returns false on duplicates; throw to give a stable error
                // site if a future contributor shadows a permission across two nested classes.
                if (!seen.Add(value))
                {
                    throw new InvalidOperationException(
                        $"Duplicate permission literal '{value}' on {nestedType.FullName}.{field.Name} " +
                        $"— permission strings must be unique across all nested classes.");
                }
            }
        }

        return seen.ToArray();
    }
}
