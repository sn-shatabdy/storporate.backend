using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Api.Authorization;

/// <summary>
/// Marks a minimal-API endpoint as requiring the named permission
/// (<c>Permissions.Jobs.Read</c>, etc.). Applied as endpoint metadata via
/// <see cref="RequirePermissionExtensions.RequirePermission"/> (the minimal-API equivalent of
/// attaching an <c>[Authorize(Policy = ...)]</c> attribute to a controller action — see the
/// STOR-62 plan, Context &amp; Findings, for why storporate.backend can't use the controller
/// pattern here).
/// </summary>
/// <remarks>
/// <para>
/// The attribute implements <see cref="IAuthorizeData"/> so
/// <see cref="AuthorizationPolicyMarkerExtensions.AddAuthorizationPolicies"/> can hand it
/// straight to ASP.NET Core's authorization metadata pipeline; the framework reads
/// <see cref="IAuthorizeData.Policy"/> off every authorization-data-bearing attribute on
/// the endpoint and resolves each one via the registered <see cref="IAuthorizationPolicyProvider"/>.
/// That makes the wiring identical to the controller-based case: the attribute carries the
/// policy name, the policy provider translates it, the handler enforces it.
/// </para>
/// <para>
/// <see cref="PolicyPrefix"/> (<c>"perm:"</c>) namespaces permission policies away from
/// any future <c>[Authorize(Policy = "...")]</c> use — e.g. a hypothetical
/// <c>PlatformAdmin</c> policy wouldn't collide with a permission literal. Every
/// <see cref="PermissionRequirement"/>-bearing policy the host ever builds starts with this
/// prefix; <see cref="PermissionPolicyProvider"/> uses it to decide whether a given policy
/// name is "ours" (build a <see cref="PermissionRequirement"/>-bearing policy) or
/// "someone else's" (delegate to <see cref="DefaultAuthorizationPolicyProvider"/>).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : Attribute, IAuthorizeData
{
    /// <summary>Namespace prefix for every policy name this attribute produces. Consumed by
    /// <see cref="PermissionPolicyProvider"/> to distinguish permission policies from any
    /// other authorization policy registered in the host.</summary>
    public const string PolicyPrefix = "perm:";

    /// <inheritdoc />
    /// <remarks>Set by the constructor to <see cref="PolicyPrefix"/> + <paramref name="permission"/>,
    /// so ASP.NET Core's standard authorization metadata pipeline resolves it via
    /// <see cref="PermissionPolicyProvider.GetPolicyAsync(string)"/> rather than
    /// <see cref="DefaultAuthorizationPolicyProvider"/>.</remarks>
    public string? Policy { get; set; }

    /// <inheritdoc />
    /// <remarks>Always <see langword="null"/> — every permission policy is built with
    /// <c>RequireAuthenticatedUser()</c> baked in by <see cref="PermissionPolicyProvider"/>,
    /// not configured per-attribute.</remarks>
    public string? Roles { get; set; }

    /// <inheritdoc />
    /// <remarks>Always <see langword="null"/> — the framework's authenticated-scheme
    /// authentication handler is what this attribute rides on, configured once in
    /// <c>Program.cs</c>'s <c>AddJwtBearer</c> call.</remarks>
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

        // Setting Policy in the ctor (and leaving it mutable for IAuthorizeData's interface
        // contract) means the value is on the attribute instance the moment it lands on the
        // endpoint's Metadata list — important for the Phase 6 coverage test, which reflects
        // over RequirePermissionAttribute instances on the EndpointDataSource rather than
        // re-resolving through the policy provider.
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
    /// Implementation: <see cref="AuthorizationEndpointConventionBuilderExtensions.RequireAuthorization(Microsoft.AspNetCore.Builder.IEndpointConventionBuilder, string)"/>
    /// is what wires the resulting <see cref="IAuthorizeData"/> into the framework's authorization
    /// pipeline; calling it with a constructed attribute is the documented minimal-API equivalent
    /// of an attribute on a controller method (the framework reads <see cref="IAuthorizeData.Policy"/>
    /// off the supplied data and resolves it via the registered
    /// <see cref="IAuthorizationPolicyProvider"/>). We also explicitly attach the attribute as
    /// metadata via <see cref="IEndpointConventionBuilder.WithMetadata"/> so the Phase 6 coverage
    /// test can find it by type without going through the policy-resolution path.
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
