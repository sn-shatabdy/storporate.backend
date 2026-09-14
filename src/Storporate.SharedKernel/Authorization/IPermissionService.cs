namespace Storporate.SharedKernel.Authorization;

/// <summary>
/// Resolves the calling user's permission set against the platform's <see cref="Permissions"/>
/// / <see cref="SystemRoles"/> model. Implemented in the SecurityGovernance module so the
/// authorization layer is owned by the module responsible for it (and the SharedKernel stays
/// free of any Infrastructure / Modules references — the contract is pure data).
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="accountId"/> is the ambient account whose workspace the permission check
/// applies in. For self-service accounts it equals <paramref name="userId"/> (workspace =
/// account, see the STOR-62 plan); for <see cref="Entities.ActorTypes.Administrator"/> it is
/// passed for audit / logging but does not constrain the result (Administrator's grant set is
/// <see cref="Permissions.All"/> regardless of the account argument).
/// </para>
/// <para>
/// Implementations are registered as <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped"/>
/// rather than <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>
/// so per-request state (e.g. ambient account-context caching, if a future story adds it)
/// cannot leak across users.
/// </para>
/// </remarks>
public interface IPermissionService
{
    /// <summary>
    /// Returns <see langword="true"/> iff the given user has the given permission in the given
    /// account's workspace. <see cref="CancellationToken"/> is forwarded to the underlying
    /// user-lookup so long-running callers can cancel cleanly.
    /// </summary>
    Task<bool> HasPermissionAsync(
        Guid userId,
        Guid accountId,
        string permission,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the full permission set the given user holds in the given account's workspace.
    /// Empty set (not <see langword="null"/>) when the user has no grants; the implementation
    /// resolves the user's <see cref="Entities.ActorTypes"/> to <see cref="SystemRoles.Grants"/>
    /// directly, with no per-user assignment table.
    /// </summary>
    Task<IReadOnlySet<string>> GetPermissionsAsync(
        Guid userId,
        Guid accountId,
        CancellationToken cancellationToken);
}
