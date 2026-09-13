using System.ComponentModel.DataAnnotations;

namespace Storporate.Infrastructure.Security;

/// <summary>
/// Configuration for the chained per-email + per-IP OTP rate limiter (see
/// <c>Storporate.Infrastructure.Security.RateLimiting.OtpRateLimiterPolicy</c>), bound to the
/// "OtpRateLimit" section. Defaults follow the plan's suggested industry-standard numbers (a
/// three-knob token-bucket reference implementation was found in ~/AIT/Docomate, but its shape —
/// a separate replenishment period distinct from the bucket's own window — doesn't map cleanly
/// onto this simpler two-knob-per-limiter contract, so the per-IP bucket here refills fully once
/// per <see cref="PerIpWindowMinutes"/> rather than trickling in continuously): 3 requests per 10
/// minutes per email (tight, since a legitimate user rarely needs more than one resend), 20
/// tokens per 10 minutes per IP (looser, since one IP can legitimately represent many users
/// behind NAT/a shared office network). Every value is operator-tunable at deploy time, not a
/// hardcoded constant.
/// </summary>
public sealed class OtpRateLimitOptions
{
    public const string SectionName = "OtpRateLimit";

    [Range(1, int.MaxValue)]
    public int PerEmailWindowMinutes { get; init; } = 10;

    [Range(1, int.MaxValue)]
    public int PerEmailMaxRequests { get; init; } = 3;

    [Range(1, int.MaxValue)]
    public int PerIpWindowMinutes { get; init; } = 10;

    [Range(1, int.MaxValue)]
    public int PerIpTokenLimit { get; init; } = 20;
}
