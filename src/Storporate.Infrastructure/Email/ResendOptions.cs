using System.ComponentModel.DataAnnotations;

namespace Storporate.Infrastructure.Email;

/// <summary>
/// Configuration for the Resend-backed <see cref="ResendEmailSender"/>, bound to the "Resend"
/// section (env vars <c>Resend__ApiKey</c>/<c>Resend__FromEmail</c>/<c>Resend__FromName</c>).
/// CONSTRAINT (plan-noted, Phase 2): no real Resend account exists yet — <see cref="ApiKey"/> is
/// still <see cref="RequiredAttribute"/>-enforced (non-empty) so the fail-fast Options pattern
/// stays consistent, but the local dev value is a placeholder, not a working key, until a real
/// Resend account is created (see the plan's Constraints/Risks section and this phase's final
/// report).
/// </summary>
public sealed class ResendOptions
{
    public const string SectionName = "Resend";

    [Required]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The verified sending address configured in the Resend account (e.g.
    /// <c>noreply@storporate.com</c>, or Resend's own sandbox sender while no custom domain is
    /// verified yet).</summary>
    [Required]
    public string FromEmail { get; init; } = string.Empty;

    public string FromName { get; init; } = "Storporate";
}
