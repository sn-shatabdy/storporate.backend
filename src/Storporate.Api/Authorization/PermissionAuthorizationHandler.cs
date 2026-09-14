using Microsoft.AspNetCore.Authorization;
using Storporate.Infrastructure.Authorization;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Api.Authorization;

/// <summary>
/// <see cref="AuthorizationHandler{TRequirement}"/> for <see cref="PermissionRequirement"/>.
/// Short-circuits to success for <see cref="SharedKernel.Entities.ActorTypes.Administrator"/>
/// callers (workspace-isolation bypass, per the STOR-62 plan, Context &amp; Findings), and
/// otherwise delegates to <see cref="IPermissionService.HasPermissionAsync"/> with the
/// ambient <see cref="IAccountContext.UserId"/> + <see cref="IAccountContext.AccountId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two reasons the Administrator bypass lives here, not in <see cref="IPermissionService"/>:
/// (1) <see cref="IPermissionService"/> is a pure data lookup — its
/// <see cref="SharedKernel.Authorization.SystemRoles.Grants"/> already grants Administrator
/// <see cref="Permissions.All"/>, so the bypass technically works there too, but
/// short-circuiting at the handler avoids the DB round-trip on every Administrator call;
/// (2) keeping it here means the same bypass path covers the same set of consumers
/// (handler, Phase 4 interceptor, Phase 5 RLS policy) all of which check the ambient
/// <see cref="IAccountContext.IsAdministrator"/> flag rather than re-deriving it.
/// </para>
/// <para>
/// Failure path: missing <see cref="IAccountContext.UserId"/> means
/// <see cref="AccountContextMiddleware"/> didn't find a <c>sub</c> claim, which in turn means
/// either authentication didn't run or the token was invalid. The policy's
/// <c>RequireAuthenticatedUser()</c> should have already short-circuited that case with a 401,
/// so reaching here without <c>UserId</c> is an unexpected state — we fail closed (no
/// <see cref="AuthorizationHandlerContext.Succeed(IAuthorizationRequirement)"/> call) and let
/// the framework return the default <see cref="AuthorizationFailure"/>.
/// </para>
/// </remarks>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IAccountContext _accountContext;
    private readonly IPermissionService _permissionService;

    public PermissionAuthorizationHandler(
        IAccountContext accountContext,
        IPermissionService permissionService)
    {
        _accountContext = accountContext;
        _permissionService = permissionService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        // Phase 3 + 4 + 5 all gate on the same "Administrator bypass" decision; the ambient
        // IsAdministrator flag (set by AccountContextMiddleware from the JWT actor_type claim)
        // is the single source of truth, so this short-circuit works identically for every
        // permission policy and every downstream consumer.
        if (_accountContext.IsAdministrator)
        {
            context.Succeed(requirement);
            return;
        }

        // No ambient user id means AccountContextMiddleware didn't find a sub claim — the
        // policy's RequireAuthenticatedUser() ought to have already turned this request away
        // with a 401. Fail closed rather than guessing.
        var userId = _accountContext.UserId;
        if (userId is null)
        {
            return;
        }

        // Self-service endpoints that don't bind {accountId} leave AccountId null; for those,
        // the call is implicitly in the caller's own workspace (= own account, per the
        // STOR-62 plan's "Workspace = account" finding), so use the userId as the accountId
        // for the permission lookup. Endpoints that bind {accountId} override this by having
        // AccountContextMiddleware write the route value into the ambient state.
        var accountId = _accountContext.AccountId ?? userId.Value;

        // AuthorizationHandlerContext has no direct hook to the per-request CancellationToken;
        // for Phase 3 the only call site is the in-process default policy-provider flow, and
        // its one DB lookup is short, so we forward CancellationToken.None. A future story
        // that needs cooperative cancel can thread HttpContext.RequestAborted in by switching
        // the handler to a constructor-injected IHttpContextAccessor.
        var hasPermission = await _permissionService.HasPermissionAsync(
            userId.Value,
            accountId,
            requirement.Permission,
            CancellationToken.None);

        if (hasPermission)
        {
            context.Succeed(requirement);
        }
    }
}
