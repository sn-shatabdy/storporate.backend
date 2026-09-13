using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Email;

namespace Storporate.Modules.Identity;

/// <summary>
/// DI entry point for the Identity module. STOR-61 Phase 2 wires up email + rate-limited OTP
/// login here; Phase 3 adds Google login and session management (refresh/logout/me/sessions)
/// alongside it.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddIdentityHandlers(this IServiceCollection services)
    {
        services.AddResendEmailSender();

        // STOR-61 Phase 3: FluentValidation validators for the new endpoints are discovered by
        // the AddValidatorsFromAssembly(typeof(...).Assembly) call in Program.cs — no per-class
        // registration needed.
        return services;
    }
}
