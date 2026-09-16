using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Infrastructure.Email;

/// <summary>
/// <see cref="IEmailSender"/> backed by a generic SMTP server via MailKit's <see cref="SmtpClient"/>.
/// STOR-64 Phase 1: interim provider used while Resend is sandbox-restricted to the account
/// owner's own email. Selected via <c>Email:Provider=Smtp</c> in <c>Program.cs</c>.
///
/// DEVIATION (plan-noted): unlike <see cref="ResendEmailSender"/>, this sender does NOT swallow
/// send failures in any environment — SMTP now has real working credentials (not a placeholder),
/// so a delivery failure is a real bug and must surface as an exception. The OTP code is still
/// logged before the send for operator convenience (e.g. reproducing a failure), regardless of
/// the send's outcome.
///
/// DEVIATION (STOR-64 follow-up fix): the certificate-validation override that tolerates an
/// incomplete revocation check is only installed in Development. In every other environment the
/// <see cref="SmtpClient"/> runs with MailKit's default strict validation, so revocation checking
/// runs normally against the server's CRL/OCSP responder. The Development override exists
/// because this Mac can't reach the CRL/OCSP responder for smtp.gmail.com's certificate chain
/// during local development; that's a local-machine problem, not something to weaken
/// production/staging TLS posture over.
///
/// Body construction (HTML + plain-text fallback) is delegated to
/// <see cref="OtpEmailTemplateBuilder"/> so every <see cref="IEmailSender"/> in this codebase
/// shares one source of truth for the email's copy and structure.
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options,
    IHostEnvironment environment,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendOtpCodeAsync(string toEmail, string code, CancellationToken cancellationToken = default)
    {
        var smtpOptions = options.Value;

        // Always log the OTP code so an operator reproducing a failure has the code on hand; the
        // Resend sender gates this to Development only because its API key is a known placeholder
        // in Development, but SMTP now has real credentials in every environment so this is safe
        // to log unconditionally (matches the plan's design decision).
        logger.LogInformation("OTP code for {Email}: {Code}", toEmail, code);

        var (html, text) = OtpEmailTemplateBuilder.Build(code);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtpOptions.FromName, smtpOptions.FromEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Your Storporate verification code";
        message.Body = new BodyBuilder
        {
            HtmlBody = html,
            TextBody = text,
        }.ToMessageBody();

        using var smtpClient = new SmtpClient();
        // DEVELOPMENT-ONLY: tolerate "incomplete certificate revocation check" failures (the
        // exact symptom MailKit's FAQ documents, and the same one .NET SslStream surfaces on
        // macOS when it can't reach a CRL/OCSP responder for the chain). In every other
        // environment we leave ServerCertificateValidationCallback at MailKit's default, so the
        // full SslStream validation runs — including revocation — and real certificate problems
        // (bad signature, untrusted root, expired, wrong name) are still rejected as before.
        if (environment.IsDevelopment())
        {
            smtpClient.ServerCertificateValidationCallback = ValidateRemoteCertificate;
        }

        // Port 587 is the STARTTLS port — explicitly opt in to STARTTLS upgrade rather than
        // implicit TLS (which is the SMTPS port 465 path). MailKit's SslOnConnect is implicit
        // TLS; StartTls is STARTTLS-after-connect.
        await smtpClient.ConnectAsync(smtpOptions.Host, smtpOptions.Port, SecureSocketOptions.StartTls, cancellationToken);
        await smtpClient.AuthenticateAsync(smtpOptions.Username, smtpOptions.Password, cancellationToken);

        // No try/catch — SMTP credentials are real, so a failure here must propagate to the
        // global exception handler rather than being silently swallowed. See class remarks.
        await smtpClient.SendAsync(message, cancellationToken);

        await smtpClient.DisconnectAsync(quit: true, cancellationToken);
    }

    private static bool ValidateRemoteCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // No errors at all — trust the default outcome.
        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        // The only failure mode we tolerate is the revocation check (RemoteCertificateChain
        // flags). Trust-chain / name / expiry errors are still rejected as real problems.
        const SslPolicyErrors onlyRevocationFailure = SslPolicyErrors.RemoteCertificateChainErrors;
        if (sslPolicyErrors != onlyRevocationFailure)
        {
            return false;
        }

        // Validate the chain itself (signature, expiry, trust root) but skip revocation — see
        // method remarks above for why this is the pragmatic choice against smtp.gmail.com on
        // macOS without outbound CRL/OCSP access.
        if (chain is null || certificate is null)
        {
            return false;
        }

        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate as X509Certificate2 ?? new X509Certificate2(certificate));
    }
}
