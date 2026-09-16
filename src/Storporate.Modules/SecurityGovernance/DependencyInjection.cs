using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Auditing;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Modules.SecurityGovernance;

/// <summary>
/// DI entry point for the SecurityGovernance module. Registers the default
/// <see cref="IPermissionService"/> and <see cref="IAuditLogWriter"/> implementations.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddSecurityGovernanceHandlers(this IServiceCollection services)
    {
        // Scoped (not singleton) so any future per-request state cannot leak across
        // requests / users.
        services.AddScoped<IPermissionService, PermissionService>();

        // Scoped for the same per-request isolation reason as PermissionService.
        services.AddScoped<IAuditLogWriter, AuditLogWriter>();
        return services;
    }
}