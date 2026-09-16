using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Storporate.Infrastructure.Auditing;
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
/// <para>
/// Both fail-closed branches now write a
/// <c>"permission_denied"</c> row to <see cref="IAuditLogWriter"/> before returning, with
/// <c>ResourceId</c> set to the checked permission literal so the future Integrity Layer
/// (STOR-45) can answer "which permission was denied for which caller?" without re-walking the
/// <c>AuthorizationHandlerContext</c>'s unrecoverable reasons. The audit row records what
/// happened (a denial occurred), not what the framework will do with it next (a 403). The
/// write happens before the early return so a denial that the framework turns into a 403
/// always leaves a row, matching the "real event happened" invariant Phase 2 pins.
/// </para>
/// </remarks>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IAccountContext _accountContext;
    private readonly IPermissionService _permissionService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuditLogWriter _auditLogWriter;

    public PermissionAuthorizationHandler(
        IAccountContext accountContext,
        IPermissionService permissionService,
        IHttpContextAccessor httpContextAccessor,
        IAuditLogWriter auditLogWriter)
    {
        _accountContext = accountContext;
        _permissionService = permissionService;
        _httpContextAccessor = httpContextAccessor;
        _auditLogWriter = auditLogWriter;
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
        // with a 401. Fail closed rather than guessing. Audit the denial before returning so
        // the row exists regardless of how the framework turns the empty context into a 403.
        var userId = _accountContext.UserId;
        if (userId is null)
        {
            await _auditLogWriter.WriteAsync(
                action: "permission_denied",
                resourceType: "Permission",
                resourceId: requirement.Permission,
                cancellationToken: _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None);
            return;
        }

        // Self-service endpoints that don't bind {accountId} leave AccountId null; for those,
        // the call is implicitly in the caller's own workspace (= own account, per the
        // STOR-62 plan's "Workspace = account" finding), so use the userId as the accountId
        // for the permission lookup. Endpoints that bind {accountId} override this by having
        // AccountContextMiddleware write the route value into the ambient state.
        var accountId = _accountContext.AccountId ?? userId.Value;

        // Forward the request's CancellationToken so a client disconnect cancels the DB
        // lookup. IHttpContextAccessor is already registered in the host (see
        // AuthorizationPoliciesExtensions), so there's no DI cost to taking it here.
        var cancellationToken = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        var hasPermission = await _permissionService.HasPermissionAsync(
            userId.Value,
            accountId,
            requirement.Permission,
            cancellationToken);

        if (hasPermission)
        {
            context.Succeed(requirement);
            return;
        }

        // Permission check returned false — same fail-closed shape as the no-UserId branch
        // above, audited identically so the future Integrity Layer sees one uniform action
        // string for "the caller's permission set didn't include this requirement".
        await _auditLogWriter.WriteAsync(
            action: "permission_denied",
            resourceType: "Permission",
            resourceId: requirement.Permission,
            cancellationToken: cancellationToken);
    }
}
