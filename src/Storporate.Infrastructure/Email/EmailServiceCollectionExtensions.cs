using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Resend;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Infrastructure.Email;

/// <summary>
/// DI entry points wiring every <see cref="IEmailSender"/> implementation in this codebase.
/// Provider selection itself lives in <c>Program.cs</c> (keyed off the <c>Email:Provider</c>
/// config value); this class only exposes one <c>Add*EmailSender()</c> method per concrete
/// implementation, so the call site reads as a 1:1 map from provider name to registration.
/// </summary>
public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Registers the official Resend SDK's <c>IResend</c> client/HttpClient plumbing (via
    /// <c>AddResend</c>) and <see cref="IEmailSender"/> as <see cref="ResendEmailSender"/>.
    /// Assumes <see cref="ResendOptions"/> is already bound and validated (see the Options-pattern
    /// registration in <c>Program.cs</c>).
    /// </summary>
    public static IServiceCollection AddResendEmailSender(this IServiceCollection services)
    {
        // The Resend SDK's own AddResend(Action&lt;ResendClientOptions&gt;) overload has no
        // service-provider-aware configure callback, so it's registered with a no-op configure
        // action here and the real API key is bridged in separately below via the DI-aware
        // OptionsBuilder.Configure&lt;TDep&gt; overload, keeping ResendOptions (not the SDK's own
        // ResendClientOptions) as the single source of truth bound from configuration.
        services.AddResend(_ => { });

        services.AddOptions<ResendClientOptions>()
            .Configure<IOptions<ResendOptions>>((resendClientOptions, resendOptions) =>
            {
                resendClientOptions.ApiToken = resendOptions.Value.ApiKey;
            });

        services.AddTransient<IEmailSender, ResendEmailSender>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IEmailSender"/> as <see cref="SmtpEmailSender"/>. Assumes
    /// <see cref="SmtpOptions"/> is already bound and validated (see the Options-pattern
    /// registration in <c>Program.cs</c>). The SMTP client itself is owned by the sender
    /// (constructed per-send inside <see cref="SmtpEmailSender"/>) — there is no shared
    /// <c>SmtpClient</c> service to register.
    /// </summary>
    public static IServiceCollection AddSmtpEmailSender(this IServiceCollection services)
    {
        services.AddTransient<IEmailSender, SmtpEmailSender>();

        return services;
    }
}
