using Microsoft.Extensions.DependencyInjection;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Modules.Identity;

/// <summary>
/// DI entry point for the Identity module. STOR-61 Phase 2 wires up email + rate-limited OTP
/// login here; Phase 3 adds Google login and session management (refresh/logout/me/sessions)
/// alongside it.
///
/// STOR-64 Phase 1: the <c>AddResendEmailSender()</c> call that previously lived here was
/// removed — provider selection now lives in <c>Program.cs</c> alongside every other
/// cross-cutting config/DI decision, keyed off the <c>Email:Provider</c> config value. The
/// Identity module is provider-agnostic; it only knows about <see cref="IEmailSender"/>.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddIdentityHandlers(this IServiceCollection services)
    {
        // STOR-61 Phase 3: FluentValidation validators for the new endpoints are discovered by
        // the AddValidatorsFromAssembly(typeof(...).Assembly) call in Program.cs — no per-class
        // registration needed.
        return services;
    }
}
