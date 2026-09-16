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

    /// <summary>Community / club account — same shape as <see cref="University"/> for STOR-62:
    /// read-only on <see cref="Entities.Job"/>. Narrowed / widened by a future story when a
    /// concrete Club-domain permission arrives.</summary>
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
    /// Maps each <see cref="SystemRoles"/> role name to its full permission set. The
    /// <see cref="Administrator"/> entry is exactly <see cref="Permissions.All"/> by
    /// construction; the other four get narrower, domain-appropriate defaults documented on
    /// the role constants above.
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
        //   - Administrator gets Permissions.All — full access across every account,
        //     enforced by the workspace-isolation bypass path (see ActorTypes remarks).
        //   - STOR-37 Phase 1: Students are the only actors who use the Portfolio
        //     surface, so the Student grant set is widened to include the full
        //     Portfolio.* triple (Create + Read + Delete). University / Club /
        //     Organization have no use for portfolio capture, so they keep their existing
        //     Jobs.Read-only grant set unchanged.
        var readOnly = new HashSet<string>(StringComparer.Ordinal) { Permissions.Jobs.Read };
        var fullJobs = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.Jobs.Read,
            Permissions.Jobs.Write,
        };
        var studentGrants = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.Jobs.Read,
            Permissions.Portfolio.Create,
            Permissions.Portfolio.Read,
            Permissions.Portfolio.Delete,
        };
        var administrator = new HashSet<string>(Permissions.All, StringComparer.Ordinal);

        // Dictionary keys reference ActorTypes.* rather than the role-name string literals
        // declared on this class so a typo in either side is caught by the compiler instead
        // of silently producing a never-looked-up grant set.
        return new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ActorTypes.Student] = studentGrants,
            [ActorTypes.Organization] = fullJobs,
            [ActorTypes.University] = readOnly,
            [ActorTypes.Club] = readOnly,
            [ActorTypes.Administrator] = administrator,
        };
    }
}
