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
