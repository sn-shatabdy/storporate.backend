using System.ComponentModel.DataAnnotations;

namespace Storporate.Infrastructure.Security;

/// <summary>
/// Configuration for OTP generation/expiry, bound to the "Otp" section. Every numeric knob here
/// is operator-tunable via environment variables rather than a hardcoded constant (see the
/// plan's rate-limit-numbers-are-flexible risk mitigation), with defaults matching the plan's
/// Phase 2 spec exactly (6-digit code, 10-minute expiry, 5 max attempts).
/// </summary>
public sealed class OtpOptions
{
    public const string SectionName = "Otp";

    [Range(4, 10)]
    public int CodeLength { get; init; } = 6;

    [Range(1, int.MaxValue)]
    public int ExpiryMinutes { get; init; } = 10;

    [Range(1, int.MaxValue)]
    public int MaxAttempts { get; init; } = 5;
}
