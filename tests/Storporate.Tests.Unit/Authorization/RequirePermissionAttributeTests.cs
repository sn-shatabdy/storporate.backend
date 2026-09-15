using Storporate.Api.Authorization;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Unit.Authorization;

/// <summary>
/// Unit coverage for <see cref="RequirePermissionAttribute"/>'s permission-catalog guard.
/// The attribute is constructed at endpoint-mapping time (effectively at host startup), so
/// throwing on an unknown permission turns a silent runtime authorization-failure-for-everyone
/// into a loud, immediate startup failure with the exact offending literal — the safest
/// direction for a typo or a permission that was renamed without updating the endpoint.
/// </summary>
public class RequirePermissionAttributeTests
{
    [Fact]
    public void Constructor_AcceptsEveryPermissionInPermissionsAll()
    {
        // For every permission the catalog knows about, construction must succeed (no throw).
        // This is the "happy path" counterpart of the typo guard below — it pins the invariant
        // that every catalog entry is constructible, so adding a new permission to the catalog
        // doesn't accidentally become un-attachable to an endpoint.
        foreach (var permission in Permissions.All)
        {
            var exception = Record.Exception(() => new RequirePermissionAttribute(permission));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void Constructor_WithUnknownPermission_ThrowsArgumentException()
    {
        // A typo / not-yet-catalogued permission must throw with the literal in the message so
        // the failure is actionable, and with ArgumentException (param name "permission") so
        // any test scaffolding can discriminate the cause.
        const string typo = "jbos:read";

        var exception = Assert.Throws<ArgumentException>(() => new RequirePermissionAttribute(typo));

        Assert.Equal("permission", exception.ParamName);
        Assert.Contains(typo, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithNullOrEmpty_StillThrowsArgumentException()
    {
        // Pre-existing contract preserved: null / empty strings fail before the catalog check.
        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException for null inputs and
        // ArgumentException for empty-string inputs (BCL behavior, not the catalog guard) — both
        // are exceptions in the System.ArgumentException family. Locks down the failure shape so
        // a future refactor that moves the guard order doesn't silently change it.
        Assert.Throws<ArgumentNullException>(() => new RequirePermissionAttribute(null!));
        Assert.Throws<ArgumentException>(() => new RequirePermissionAttribute(string.Empty));
    }

    [Fact]
    public void Constructor_WithKnownPermission_SetsPolicyWithPermPrefix()
    {
        // Confirms the catalog-accepted branch still wires the policy name correctly: the
        // PermissionPolicyProvider keys off the "perm:" prefix, so a missing prefix would
        // silently route through the wrong policy provider.
        var attribute = new RequirePermissionAttribute(Permissions.Jobs.Read);

        Assert.Equal($"perm:{Permissions.Jobs.Read}", attribute.Policy);
    }
}
