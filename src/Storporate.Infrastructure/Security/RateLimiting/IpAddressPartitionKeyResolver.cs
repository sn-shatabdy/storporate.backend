using Microsoft.AspNetCore.Http;

namespace Storporate.Infrastructure.Security.RateLimiting;

internal static class IpAddressPartitionKeyResolver
{
    private const string FallbackPartitionKey = "unknown";

    public static string Resolve(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? FallbackPartitionKey;
}
