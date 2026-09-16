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

    [Required]
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
