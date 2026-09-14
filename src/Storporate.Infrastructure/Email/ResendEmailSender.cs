using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resend;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Infrastructure.Email;

/// <summary>
/// <see cref="IEmailSender"/> backed by the official Resend .NET SDK.
/// CONSTRAINT (plan-noted, Phase 2): no real Resend account exists yet, so
/// <see cref="ResendOptions.ApiKey"/> is a placeholder in local dev and a real send will fail.
/// Two things follow from that: (1) in Development, the generated code is logged directly so
/// <c>POST /api/auth/otp/verify</c> stays manually testable end-to-end without a live inbox — see
/// this phase's final report for why that approach was chosen over reading the code back from
/// Postgres; (2) a Resend call failure is only swallowed in Development (where the code was
/// already logged above), never in Production, where a delivery failure must still surface as a
/// real error rather than silently pretending the code was sent.
/// </summary>
public sealed class ResendEmailSender(
    IResend resendClient,
    IOptions<ResendOptions> options,
    IHostEnvironment environment,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    public async Task SendOtpCodeAsync(string toEmail, string code, CancellationToken cancellationToken = default)
    {
        if (environment.IsDevelopment())
        {
            // DEV ONLY — never logs a real code outside Development.
            logger.LogInformation("[DEV ONLY] OTP code for {Email}: {Code}", toEmail, code);
        }

        var resendOptions = options.Value;
        var message = new EmailMessage
        {
            From = new EmailAddress { Email = resendOptions.FromEmail, DisplayName = resendOptions.FromName },
            To = toEmail,
            Subject = "Your Storporate verification code",
            TextBody = $"Your verification code is {code}. It expires shortly and can only be used once. "
                + "If you didn't request this, you can safely ignore this email.",
        };

        try
        {
            await resendClient.EmailSendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (environment.IsDevelopment())
        {
            logger.LogWarning(
                exception,
                "Resend email send failed in Development (no real Resend API key configured yet — "
                + "see ResendOptions/.env.example); the OTP code was already logged above for "
                + "manual verification.");
        }
    }
}
