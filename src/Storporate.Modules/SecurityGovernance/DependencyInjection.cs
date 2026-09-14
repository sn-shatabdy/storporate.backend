using Microsoft.Extensions.DependencyInjection;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Modules.SecurityGovernance;

/// <summary>
/// DI entry point for the SecurityGovernance module. STOR-62 Phase 2 registers the default
/// <see cref="IPermissionService"/> implementation here — the first real code in this module.
/// Future phases extend this entry point: Phase 3 adds the <c>IAccountContext</c>,
/// <c>IAuthorizationPolicyProvider</c>, and <c>IAuthorizationHandler</c> registrations
/// alongside it.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddSecurityGovernanceHandlers(this IServiceCollection services)
    {
        // Scoped (not singleton) so any future per-request state (ambient account-context
        // caching, audit hooks) cannot leak across requests / users. Mirrors how other
        // module-scoped services (IJwtTokenService, IGoogleIdTokenValidator) are registered
        // in Program.cs.
        services.AddScoped<IPermissionService, PermissionService>();
        return services;
    }
}
