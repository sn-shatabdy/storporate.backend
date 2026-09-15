using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Storporate.Api.Authorization;

/// <summary>
/// Translates <c>"perm:x"</c>-prefixed policy names (produced by
/// <see cref="RequirePermissionAttribute"/>) into <see cref="AuthorizationPolicy"/> objects on
/// demand, caching the result so repeated lookups for the same permission are free.
/// Non-<c>"perm:"</c>-prefixed policy names are delegated to the wrapped
/// <see cref="DefaultAuthorizationPolicyProvider"/> so any other <c>[Authorize(Policy = ...)]</c>
/// use on the host keeps working unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Registered as <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>
/// via <see cref="AuthorizationPoliciesExtensions.AddAuthorizationPolicies"/> — the
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> cache must survive across requests for the
/// optimization to mean anything, and the provider itself holds no per-request state.
/// </para>
/// <para>
/// The policy built for a permission literal is
/// <c>RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission))</c>:
/// the handler (<see cref="PermissionAuthorizationHandler"/>) is the single source of truth for
/// what "has this permission" means, so we don't also wire <see cref="ClaimsAuthorizationRequirement"/>
/// or <see cref="RolesAuthorizationRequirement"/> here — keeping the policy body minimal makes
/// the handler's success path the only thing that has to be right.
/// </para>
/// </remarks>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _cache = new(StringComparer.Ordinal);

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        // DefaultAuthorizationPolicyProvider reads FallbackPolicy / DefaultPolicy off the same
        // IOptions<AuthorizationOptions> the host built in AddAuthorization() — wrapping it
        // preserves any framework-default policies (e.g. an Administrator fallback policy added
        // in a future phase) for non-permission lookups.
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        ArgumentNullException.ThrowIfNull(policyName);

        // Non-permission policy names: hand off to the default provider unchanged. This is
        // what lets a future [Authorize(Policy = "PlatformAdmin")] (or whatever non-permission
        // policy name a future story adds) keep working without us having to special-case it.
        if (!policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        // GetOrAdd over a ConcurrentDictionary is the right primitive here: two requests racing
        // to build the same policy may both end up in the factory, but the cache always ends
        // up with one consistent value, and the racing factory invocations are cheap.
        var policy = _cache.GetOrAdd(policyName, static name =>
        {
            var permission = name[RequirePermissionAttribute.PolicyPrefix.Length..];
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();
        });

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}
