using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Storporate.Infrastructure.Security.RateLimiting;

/// <summary>
/// Adapts a combined <see cref="PartitionedRateLimiter{HttpContext}"/> (built via
/// <see cref="PartitionedRateLimiter.CreateChained{TResource}"/>) into the single per-request
/// <see cref="RateLimiter"/> instance that <c>RateLimiterOptions.AddPolicy</c> expects for a given
/// <see cref="HttpContext"/>. This exact adapter shape (ASP.NET Core's rate-limiting partition API
/// otherwise has no built-in way to chain two differently-keyed limiters under one named policy)
/// was verified against ~/AIT/Docomate's own proven <c>HttpContextDelegatingRateLimiter</c> before
/// being reimplemented here, per the plan's explicit guidance to read that reference rather than
/// guess at the generic constraints from memory.
/// </summary>
internal sealed class HttpContextDelegatingRateLimiter(
    PartitionedRateLimiter<HttpContext> inner,
    HttpContext context) : RateLimiter
{
    private readonly DateTime _createdAt = DateTime.UtcNow;

    public override TimeSpan? IdleDuration => DateTime.UtcNow - _createdAt;

    public override RateLimiterStatistics? GetStatistics() => inner.GetStatistics(context);

    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        inner.AttemptAcquire(context, permitCount);

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        inner.AcquireAsync(context, permitCount, cancellationToken);
}
