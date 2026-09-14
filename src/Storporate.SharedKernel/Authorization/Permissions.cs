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

                // SortedSet.Add returns false on duplicates rather than throwing — that gives
                // us a free dedupe + a stable error site if a future contributor accidentally
                // shadows an existing permission string across two nested classes.
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
