using Microsoft.Extensions.DependencyInjection;

namespace Storporate.Modules.InstitutionalClubNetwork;

/// <summary>
/// DI entry point for the InstitutionalClubNetwork module. The club profile handlers are static and
/// their validators are picked up by the assembly-wide <c>AddValidatorsFromAssembly(...)</c> call
/// in <c>Program.cs</c>, so nothing needs registering yet; the hook exists so later slices
/// (background processors, services) have one place to register without touching <c>Program.cs</c>
/// structure.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInstitutionalClubNetworkHandlers(this IServiceCollection services) =>
        services;
}
