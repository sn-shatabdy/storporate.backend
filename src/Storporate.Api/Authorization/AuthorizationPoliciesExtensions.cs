using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Storporate.Infrastructure.Authorization;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Api.Authorization;

/// <summary>
/// DI entry point for the STOR-62 Phase 3 authorization stack. Wires the ambient account
/// context, the custom policy provider, and the custom authorization handler into the same
/// <see cref="AuthorizationOptions"/> the host's <c>AddAuthorization()</c> already configured
/// — calling this <em>after</em> <c>AddAuthorization()</c> in <c>Program.cs</c> is part of
/// the wiring contract.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><see cref="IHttpContextAccessor"/>: the standard accessor — Phase 4/5's EF Core
///   interceptor and any handler that wants <c>HttpContext.RequestAborted</c> pulls it from
///   here. The Phase 3 handler itself doesn't use it.</item>
///   <item><see cref="IAccountContext"/> + <see cref="IAccountContextWriter"/>: the singleton
///   ambient holder. Singleton (not scoped) is required because <see cref="AmbientAccountContext"/>'s
///   <see cref="AsyncLocal{T}"/>-backed storage must survive across the per-request scope to
///   reach consumers the per-request scope doesn't see (EF Core's pipeline, in particular).</item>
///   <item><see cref="IPermissionService"/>: not registered here — the SecurityGovernance
///   module's <c>AddSecurityGovernanceHandlers()</c> already registers it, and depending on
///   the concrete implementation from the API project would force a downward module
///   reference. <c>Program.cs</c> calls <c>AddSecurityGovernanceHandlers()</c> before
///   <c>AddAuthorizationPolicies()</c>, so by the time the handler runs the service is
///   already in the container.</item>
///   <item><see cref="PermissionAuthorizationHandler"/>: registered as a transient handler
///   alongside the default <see cref="IAuthorizationHandlerProvider"/> flow — matches how the
///   framework treats handler dependencies (the provider resolves them per authorization
///   check). Transient (not singleton) is required because the handler depends on the scoped
///   <see cref="IPermissionService"/> and DI rejects singleton-with-scoped-dependency at
///   host build time.</item>
///   <item><see cref="PermissionPolicyProvider"/>: registered as a singleton, replacing the
///   default <see cref="IAuthorizationPolicyProvider"/> registered by
///   <c>AddAuthorization()</c>. <see cref="ServiceCollectionDescriptorExtensions.Replace(IServiceCollection,ServiceDescriptor)"/>
///   over an existing descriptor does exactly that.</item>
/// </list>
/// </remarks>
public static class AuthorizationPoliciesExtensions
{
    public static IServiceCollection AddAuthorizationPolicies(this IServiceCollection services)
    {
        // IHttpContextAccessor: standard minimal-API accessor. The handler uses it to
        // forward the request's RequestAborted as the cancellation token for its
        // IPermissionService lookup (see PermissionAuthorizationHandler.HandleRequirementAsync)
        // so a client disconnect cancels the in-flight DB call. Registering it here keeps
        // a single place that owns the accessor registration.
        services.AddHttpContextAccessor();

        // Ambient account context: singleton so its AsyncLocal storage spans the request's
        // scope and every consumer (the handler in this phase; the EF Core global query
        // filter and interceptor in Phase 4; the RLS GUC writer in Phase 5) sees the same
        // values without having to register the service in their own DI registration path.
        services.TryAddSingleton<AmbientAccountContext>();
        services.TryAddSingleton<IAccountContextWriter>(sp => sp.GetRequiredService<AmbientAccountContext>());
        services.TryAddSingleton<IAccountContext>(sp => sp.GetRequiredService<AmbientAccountContext>());

        // The handler: registered as transient. The handler holds no per-request state of its
        // own, but it depends on IPermissionService (registered scoped by SecurityGovernance)
        // which a singleton handler would capture once at startup — so the validation engine
        // rejects the wiring at host build time. Transient is the canonical lifetime for
        // IAuthorizationHandler (the framework resolves it per authorization check via
        // IAuthorizationHandlerProvider, so per-check instance allocation is cheap and matches
        // how the framework's default DI helper wires handlers).
        services.AddTransient<IAuthorizationHandler, PermissionAuthorizationHandler>();

        // Policy provider: replace the default one IAuthorizationPolicyProvider registered,
        // so the framework resolves "perm:..." policies through PermissionPolicyProvider
        // while leaving every other policy name untouched.
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>());

        return services;
    }
}
