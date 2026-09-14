using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace Storporate.Infrastructure.Security.RateLimiting;

/// <summary>
/// Chained per-email fixed-window + per-IP token-bucket rate limiter for the OTP endpoints
/// (<c>POST /api/auth/otp/request</c> and <c>/verify</c>), sourced from
/// <see cref="OtpRateLimitOptions"/>. Applied to those endpoints via
/// <c>.RequireRateLimiting(OtpRateLimiterPolicy.PolicyName)</c> (the minimal-API equivalent of the
/// MVC <c>[EnableRateLimiting]</c> attribute). The chaining approach (two independently-keyed
/// limiters combined via <see cref="PartitionedRateLimiter.CreateChained{TResource}"/>, adapted
/// for <c>AddPolicy&lt;HttpContext&gt;</c> via <see cref="HttpContextDelegatingRateLimiter"/>) was
/// verified against ~/AIT/Docomate's own proven <c>OtpRateLimiterPolicy</c> before being
/// reimplemented here, per the plan's explicit guidance.
/// </summary>
public static class OtpRateLimiterPolicy
{
    public const string PolicyName = "otp";

    public static void AddOtpCombinedPolicy(this RateLimiterOptions options, OtpRateLimitOptions rateLimitOptions)
    {
        var combinedLimiter = PartitionedRateLimiter.CreateChained(
            CreateEmailLimiter(rateLimitOptions),
            CreateIpLimiter(rateLimitOptions));

        options.AddPolicy<HttpContext>(PolicyName, httpContext =>
            RateLimitPartition.Get(httpContext, key => new HttpContextDelegatingRateLimiter(combinedLimiter, key)));
    }

    private static PartitionedRateLimiter<HttpContext> CreateEmailLimiter(OtpRateLimitOptions rateLimitOptions) =>
        PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: OtpRateLimitEmailCaptureMiddleware.ResolveCapturedEmail(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimitOptions.PerEmailMaxRequests,
                    Window = TimeSpan.FromMinutes(rateLimitOptions.PerEmailWindowMinutes),
                    QueueLimit = 0,
                }));

    private static PartitionedRateLimiter<HttpContext> CreateIpLimiter(OtpRateLimitOptions rateLimitOptions) =>
        PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            RateLimitPartition.GetTokenBucketLimiter(
                partitionKey: IpAddressPartitionKeyResolver.Resolve(httpContext),
                factory: _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = rateLimitOptions.PerIpTokenLimit,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(rateLimitOptions.PerIpWindowMinutes),
                    TokensPerPeriod = rateLimitOptions.PerIpTokenLimit,
                    QueueLimit = 0,
                }));
}
