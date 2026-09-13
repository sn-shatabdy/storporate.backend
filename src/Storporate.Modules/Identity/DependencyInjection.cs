using Microsoft.Extensions.DependencyInjection;

namespace Storporate.Modules.Identity;

/// <summary>
/// DI entry point for the Identity module. Empty-bodied for STOR-61 Phase 1 — no handlers exist
/// yet — but wired up now so later phases (OTP request/verify, Google login, session management)
/// add registrations here instead of inventing a new module wiring pattern.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddIdentityHandlers(this IServiceCollection services)
    {
        return services;
    }
}
