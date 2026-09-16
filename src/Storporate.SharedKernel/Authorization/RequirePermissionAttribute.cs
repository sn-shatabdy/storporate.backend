using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

namespace Storporate.SharedKernel.Authorization;

/// <summary>
/// Marks a minimal-API endpoint as requiring the named permission
/// (<c>Permissions.Jobs.Read</c>, etc.). Applied as endpoint metadata via
/// <see cref="RequirePermissionExtensions.RequirePermission"/> (the minimal-API equivalent of
/// attaching an <c>[Authorize(Policy = ...)]</c> attribute to a controller action).
/// </summary>
/// <remarks>
/// <para>
/// The attribute implements <see cref="IAuthorizeData"/> so ASP.NET Core's
/// authorization metadata pipeline can hand it straight to the framework; the framework
/// reads <see cref="IAuthorizeData.Policy"/> off every authorization-data-bearing
/// attribute on the endpoint and resolves each one via the registered
/// <see cref="IAuthorizationPolicyProvider"/>. That makes the wiring identical to the
/// controller-based case: the attribute carries the policy name, the policy provider
/// translates it, the handler enforces it.
/// </para>
/// <para>
/// <see cref="PolicyPrefix"/> (<c>"perm:"</c>) namespaces permission policies away from
/// any future <c>[Authorize(Policy = "...")]</c> use. Every
/// <c>PermissionRequirement</c>-bearing policy the host ever builds starts with this
/// prefix.
/// </para>
/// <para>
/// Originally lived in <c>Storporate.Api.Authorization</c> alongside the policy provider
/// and authorization handler that consume it; relocated to <c>SharedKernel</c> in
/// <c>STOR-63 Phase 3</c> so module-level endpoints (e.g.
/// <c>SecurityGovernance/AuditLogEndpoints.cs</c>) can call
/// <see cref="RequirePermissionExtensions.RequirePermission"/> without taking a
/// backward reference from Modules into Api.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : Attribute, IAuthorizeData
{
    /// <summary>Namespace prefix for every policy name this attribute produces.</summary>
    public const string PolicyPrefix = "perm:";

    /// <inheritdoc />
    public string? Policy { get; set; }

    /// <inheritdoc />
    public string? Roles { get; set; }

    /// <inheritdoc />
    public string? AuthenticationSchemes { get; set; }

    public RequirePermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrEmpty(permission);

        // Fail-fast against the permission catalog: a typo like "jbos:read" would otherwise
        // compile, attach to the endpoint, and silently fail closed (handler returns false) for
        // every caller at runtime — a security- and productivity-relevant silent contract
        // change. Because every RequirePermission(...) call runs at endpoint-mapping time
        // (effectively at app startup), throwing here surfaces the typo with the exact
        // offending literal before any request is served. The check uses Permissions.All
        // (built once at class-init from reflection over every nested const string), so a
        // permission added to a nested Permissions.* class is automatically recognized.
        if (!Permissions.All.Contains(permission))
        {
            throw new ArgumentException(
                $"'{permission}' is not a registered permission. Expected one of: "
                + string.Join(", ", Permissions.All)
                + ". Add the literal to a Permissions.<area> nested class first.",
                nameof(permission));
        }

        Policy = PolicyPrefix + permission;
    }
}

/// <summary>
/// Minimal-API extension that attaches a <see cref="RequirePermissionAttribute"/> as endpoint
/// metadata. Equivalent to <c>[Authorize(Policy = "perm:...")]</c> on a controller action;
/// use from any <c>MapXxx(...).RequirePermission(Permissions.Jobs.Read)</c> chain.
/// </summary>
public static class RequirePermissionExtensions
{
    /// <summary>Attaches a <see cref="RequirePermissionAttribute"/> for the named permission
    /// to the endpoint the <paramref name="builder"/> is configuring. Returns the same
    /// <see cref="RouteHandlerBuilder"/> so it composes with the standard
    /// <c>RequireAuthorization</c>/<c>WithName</c>/<c>WithTags</c>/etc. chain.
    /// <para>
    /// Implementation: <c>RequireAuthorization</c> on
    /// <see cref="IEndpointConventionBuilder"/> is what wires the resulting
    /// <see cref="IAuthorizeData"/> into the framework's authorization pipeline; calling
    /// it with a constructed attribute is the documented minimal-API equivalent of an
    /// attribute on a controller method. We also explicitly attach the attribute as
    /// metadata via <see cref="IEndpointConventionBuilder.WithMetadata"/> so the
    /// permission-coverage test can find it by type without going through the
    /// policy-resolution path.
    /// </para>
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : notnull, IEndpointConventionBuilder
    {
        var attribute = new RequirePermissionAttribute(permission);
        builder.WithMetadata(attribute);
        return builder.RequireAuthorization(attribute);
    }
}
