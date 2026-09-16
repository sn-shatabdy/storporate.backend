using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Storporate.Api.Authorization;
using Storporate.Infrastructure.Authorization;
using Storporate.SharedKernel.Authorization;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Authorization;

/// <summary>
/// Direct unit coverage for <see cref="PermissionAuthorizationHandler"/>'s audit-log
/// instrumentation. Unlike <see cref="RequirePermissionEndpointTests"/>, these tests don't
/// spin up <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> —
/// they instantiate the handler with a fake <see cref="IAccountContext"/>, a fake
/// <see cref="IPermissionService"/>, and a fake <see cref="IHttpContextAccessor"/>, and
/// drive <see cref="AuthorizationHandler{TRequirement}.HandleAsync"/> directly. The point
/// of Phase 2's tests is to pin which audit row (if any) each fail-closed branch writes —
/// the endpoint-level integration coverage above already proves the framework wiring still
/// produces 403s; here we prove the audit row is the right shape.
/// </summary>
public class PermissionAuthorizationHandlerTests
{
    [Fact]
    public async Task HandleAsync_NoAmbientUserId_RecordsPermissionDenied()
    {
        // STOR-63 Phase 2 acceptance criterion (a): no ambient UserId means the handler
        // can't ask the permission service anything meaningful, so it fails closed and
        // MUST record exactly one "permission_denied" row keyed off the requirement's
        // permission literal — that's the only field safe to surface in the log when
        // there is no caller identity.
        var accountContext = new FakeAccountContext { UserId = null };
        var permissionService = new FakePermissionService();
        var auditLogWriter = new FakeAuditLogWriter();
        var httpContextAccessor = new FakeHttpContextAccessor();
        var handler = new PermissionAuthorizationHandler(
            accountContext, permissionService, httpContextAccessor, auditLogWriter);

        var requirement = new PermissionRequirement("jobs:write");
        var handlerContext = new AuthorizationHandlerContext(
            [requirement],
            user: new System.Security.Claims.ClaimsPrincipal(),
            resource: null);

        await handler.HandleAsync(handlerContext);

        Assert.False(handlerContext.HasSucceeded);

        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("permission_denied", entry.Action);
        Assert.Equal("Permission", entry.ResourceType);
        Assert.Equal("jobs:write", entry.ResourceId);
        Assert.Null(entry.MetadataJson);

        // PermissionService must not have been consulted — there's no UserId to pass to it.
        Assert.Equal(0, permissionService.CallCount);
    }

    [Fact]
    public async Task HandleAsync_PermissionCheckReturnsFalse_RecordsPermissionDenied()
    {
        // STOR-63 Phase 2 acceptance criterion (b): when there's an ambient user but the
        // permission service says they don't hold the requirement, the handler fails closed
        // and MUST record one "permission_denied" row — same shape as the no-UserId branch,
        // because from the audit log's point of view the user-experience is identical (a
        // 403 from the framework).
        var userId = Guid.NewGuid();
        var accountContext = new FakeAccountContext { UserId = userId };
        var permissionService = new FakePermissionService { HasPermissionResult = false };
        var auditLogWriter = new FakeAuditLogWriter();
        var httpContextAccessor = new FakeHttpContextAccessor();
        var handler = new PermissionAuthorizationHandler(
            accountContext, permissionService, httpContextAccessor, auditLogWriter);

        var requirement = new PermissionRequirement("audit-log:view");
        var handlerContext = new AuthorizationHandlerContext(
            [requirement],
            user: new System.Security.Claims.ClaimsPrincipal(),
            resource: null);

        await handler.HandleAsync(handlerContext);

        Assert.False(handlerContext.HasSucceeded);

        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("permission_denied", entry.Action);
        Assert.Equal("Permission", entry.ResourceType);
        Assert.Equal("audit-log:view", entry.ResourceId);

        Assert.Equal(1, permissionService.CallCount);
        Assert.Equal(userId, permissionService.LastUserId);
        Assert.Equal("audit-log:view", permissionService.LastPermission);
    }

    [Fact]
    public async Task HandleAsync_PermissionCheckReturnsTrue_DoesNotRecordPermissionDenied()
    {
        // Sanity check (not in the plan's AC list, but pins the negative case): when the
        // permission service says yes, the handler succeeds and records NO "permission_denied"
        // row. A "permission_granted" action isn't part of Phase 2's action catalog, by
        // design — Phase 3's UI only needs to show denials.
        var userId = Guid.NewGuid();
        var accountContext = new FakeAccountContext { UserId = userId };
        var permissionService = new FakePermissionService { HasPermissionResult = true };
        var auditLogWriter = new FakeAuditLogWriter();
        var httpContextAccessor = new FakeHttpContextAccessor();
        var handler = new PermissionAuthorizationHandler(
            accountContext, permissionService, httpContextAccessor, auditLogWriter);

        var requirement = new PermissionRequirement("jobs:read");
        var handlerContext = new AuthorizationHandlerContext(
            [requirement],
            user: new System.Security.Claims.ClaimsPrincipal(),
            resource: null);

        await handler.HandleAsync(handlerContext);

        Assert.True(handlerContext.HasSucceeded);
        Assert.Empty(auditLogWriter.Recorded);
    }

    [Fact]
    public async Task HandleAsync_AdministratorBypass_DoesNotRecordPermissionDenied()
    {
        // Administrator callers short-circuit before the permission check runs (see the
        // existing handler doc comment), so they must NOT produce a "permission_denied"
        // row. Without this test, a future refactor that accidentally moved the audit
        // write ahead of the Administrator bypass would start spamming the audit log
        // every time an Administrator hits any [RequirePermission]-tagged endpoint.
        var accountContext = new FakeAccountContext { UserId = Guid.NewGuid(), IsAdministrator = true };
        var permissionService = new FakePermissionService { HasPermissionResult = false };
        var auditLogWriter = new FakeAuditLogWriter();
        var httpContextAccessor = new FakeHttpContextAccessor();
        var handler = new PermissionAuthorizationHandler(
            accountContext, permissionService, httpContextAccessor, auditLogWriter);

        var requirement = new PermissionRequirement("any-permission");
        var handlerContext = new AuthorizationHandlerContext(
            [requirement],
            user: new System.Security.Claims.ClaimsPrincipal(),
            resource: null);

        await handler.HandleAsync(handlerContext);

        Assert.True(handlerContext.HasSucceeded);
        Assert.Empty(auditLogWriter.Recorded);

        // PermissionService must not have been consulted on the Administrator short-circuit.
        Assert.Equal(0, permissionService.CallCount);
    }

    /// <summary>
    /// Hand-written <see cref="IAccountContext"/> test double — the handler's existing tests
    /// all use <see cref="AmbientAccountContext"/>, but the AmbientAccountContext's
    /// AsyncLocal semantics don't matter for these handler tests (we set the values and
    /// immediately read them on the same async-flow line) and we want to keep the test
    /// setup minimal so each test's input shape is obvious from the test body.
    /// </summary>
    private sealed class FakeAccountContext : IAccountContext
    {
        public Guid? UserId { get; init; }
        public Guid? AccountId { get; init; }
        public bool IsAdministrator { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
    }

    /// <summary>
    /// Test double for <see cref="IPermissionService"/> that records the last call and
    /// returns a configurable <see cref="HasPermissionResult"/>.
    /// </summary>
    private sealed class FakePermissionService : IPermissionService
    {
        public bool HasPermissionResult { get; init; }

        public int CallCount { get; private set; }

        public Guid LastUserId { get; private set; }

        public Guid LastAccountId { get; private set; }

        public string? LastPermission { get; private set; }

        public Task<bool> HasPermissionAsync(
            Guid userId,
            Guid accountId,
            string permission,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastUserId = userId;
            LastAccountId = accountId;
            LastPermission = permission;
            return Task.FromResult(HasPermissionResult);
        }

        public Task<IReadOnlySet<string>> GetPermissionsAsync(
            Guid userId,
            Guid accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(
                HasPermissionResult
                    ? new HashSet<string>(StringComparer.Ordinal) { LastPermission ?? string.Empty }
                    : new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Minimal <see cref="IHttpContextAccessor"/> — the handler forwards the request's
    /// <c>RequestAborted</c> token through it to the audit writer, so we provide a fresh
    /// <see cref="CancellationToken.None"/> source.
    /// </summary>
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
