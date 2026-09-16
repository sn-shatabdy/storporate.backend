using System.ComponentModel.DataAnnotations;

namespace Storporate.Infrastructure.Email;

/// <summary>
/// Configuration for the SMTP-backed <see cref="SmtpEmailSender"/>, bound to the "Smtp" section
/// (env vars <c>Smtp__Host</c>/<c>Smtp__Port</c>/<c>Smtp__Username</c>/<c>Smtp__Password</c>/
/// <c>Smtp__FromEmail</c>/<c>Smtp__FromName</c>).
/// STOR-64 Phase 1: interim Gmail-SMTP provider, used while Resend is sandbox-restricted to
/// only the account owner's own email. Selected via <c>Email:Provider</c> in
/// <c>Program.cs</c>.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required]
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// TCP port for the SMTP server. <c>[Range(1, 65535)]</c> matches the project's convention
    /// for int config values (see <c>OtpRateLimitOptions</c>'s analogous knobs) and rules out a
    /// missing/empty <c>Smtp__Port</c> binding to <c>0</c>, which would otherwise fail deep in
    /// MailKit with a confusing error instead of failing fast at startup.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; init; }

    [Required]
    public string Username { get; init; } = string.Empty;

    [Required]
    public string Password { get; init; } = string.Empty;

    /// <summary>The sending address authenticated against <see cref="Username"/>.</summary>
    [Required]
    public string FromEmail { get; init; } = string.Empty;

    public string FromName { get; init; } = "Storporate";
}
