using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Authorization;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.SecurityGovernance;

/// <summary>
/// Default <see cref="IPermissionService"/> implementation. A user's role is derived directly
/// from their <see cref="User.ActorType"/> (no join table, no per-user role assignment — see
/// the STOR-62 plan, Context &amp; Findings), so the only DB lookup is "find the user by id
/// and read their <see cref="User.ActorType"/>"; the permission set itself is computed from
/// <see cref="SystemRoles.Grants"/> in memory.
/// </summary>
/// <remarks>
/// <para>
/// Registered as <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped"/>
/// (see <see cref="DependencyInjection"/>) rather than singleton to avoid any chance of
/// cross-request state leakage.
/// </para>
/// <para>
/// The <paramref name="accountId"/> parameter is accepted (and resolved to a
/// <see cref="HashSet{T}"/>) for API symmetry with the future ambient-account-context story
/// (Phase 3): when an Administrator acts "as" another account, the resolved set will need to
/// reflect that account's role grants. In Phase 2, with no ambient-account plumbing yet, it is
/// not used beyond signature compatibility.
/// </para>
/// </remarks>
public sealed class PermissionService(WriteDbContext dbContext) : IPermissionService
{
    private readonly WriteDbContext _dbContext = dbContext;

    public async Task<bool> HasPermissionAsync(
        Guid userId,
        Guid accountId,
        string permission,
        CancellationToken cancellationToken)
    {
        var grants = await ResolveGrantsAsync(userId, cancellationToken);
        return grants.Contains(permission);
    }

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(
        Guid userId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        return await ResolveGrantsAsync(userId, cancellationToken);
    }

    /// <summary>
    /// Single point of database access for the service: one <c>SELECT ActorType</c> per call,
    /// shared between <see cref="HasPermissionAsync"/> and <see cref="GetPermissionsAsync"/>.
    /// Factoring it out prevents the two public methods from drifting into different queries
    /// over time and guarantees they always reflect the same DB snapshot.
    /// </summary>
    private async Task<IReadOnlySet<string>> ResolveGrantsAsync(Guid userId, CancellationToken cancellationToken)
    {
        // AsNoTracking: this is a pure authorization lookup, no entity-change tracking needed.
        // We only need ActorType to map to SystemRoles.Grants — no further mutations.
        var actorType = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.ActorType)
            .SingleOrDefaultAsync(cancellationToken);

        // No matching user (or user with no ActorType recorded) gets an empty grant set —
        // authorization fails closed. Same for the (currently impossible) null ActorType;
        // the DB column is non-nullable and required at insert.
        if (actorType is null || !SystemRoles.Grants.TryGetValue(actorType, out var granted))
        {
            return EmptyGrants;
        }

        // SystemRoles.Grants already returns HashSet<string> with StringComparer.Ordinal, so
        // we return it as-is — the IReadOnlySet<string> contract is satisfied without copying
        // a static, never-mutated dictionary value just to look like a fresh HashSet.
        return granted;
    }

    private static readonly IReadOnlySet<string> EmptyGrants = new HashSet<string>(StringComparer.Ordinal);
}
