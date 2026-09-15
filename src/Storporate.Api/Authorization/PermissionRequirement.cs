using Microsoft.AspNetCore.Authorization;

namespace Storporate.Api.Authorization;

/// <summary>
/// Single authorization requirement carrying the permission literal being checked (e.g.
/// <c>Permissions.Jobs.Read</c>). Materialized into a real <see cref="AuthorizationPolicy"/>
/// by <see cref="PermissionPolicyProvider"/> when an endpoint declares a
/// <see cref="RequirePermissionAttribute"/> with the corresponding <c>perm:</c>-prefixed
/// policy name.
/// </summary>
/// <remarks>
/// Kept deliberately small (one field, one constructor) — the requirements a handler can
/// succeed against are encoded by the policy itself
/// (<c>RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(...))</c>), not
/// on this type. Adding a new requirement shape would be a new <see cref="IAuthorizationRequirement"/>
/// subclass with its own <see cref="IAuthorizationHandler{TRequirement}"/>.
/// </remarks>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    /// <summary>The permission literal being checked. Compared ordinally (case-sensitive)
    /// against the caller's resolved permission set, mirroring the comparer used when
    /// building <see cref="SharedKernel.Authorization.Permissions.All"/>.</summary>
    public string Permission { get; }

    public PermissionRequirement(string permission)
    {
        ArgumentException.ThrowIfNullOrEmpty(permission);
        Permission = permission;
    }
}
