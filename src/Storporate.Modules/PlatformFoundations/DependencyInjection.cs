using Microsoft.Extensions.DependencyInjection;

namespace Storporate.Modules.PlatformFoundations;

/// <summary>
/// DI entry point for the Platform Foundations module. Empty-bodied for STOR-59 — no handlers
/// exist yet — but wired up now so later stories add registrations here instead of inventing a
/// new module wiring pattern.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddPlatformFoundationsHandlers(this IServiceCollection services)
    {
        return services;
    }
}
