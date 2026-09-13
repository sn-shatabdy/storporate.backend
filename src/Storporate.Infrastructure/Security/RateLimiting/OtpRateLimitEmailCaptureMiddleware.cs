using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Storporate.Infrastructure.Security.RateLimiting;

/// <summary>
/// Reads the <c>email</c> field out of the OTP endpoints' JSON request body and stashes it on
/// <see cref="HttpContext.Items"/> before <c>app.UseRateLimiter()</c> runs. The rate-limiting
/// middleware sits ahead of minimal-API model binding in the pipeline, so the per-email
/// partition-key function (<see cref="OtpRateLimiterPolicy"/>) has no other way to see the parsed
/// request body at the point it needs the key. Buffers the request so the endpoint's own model
/// binder can still read the body afterwards. Mirrors ~/AIT/Docomate's own proven
/// <c>OtpRateLimitPartitionKeyMiddleware</c>/<c>EmailPartitionKeyResolver</c> pattern, read before
/// implementing this rather than guessed from memory (per the plan's explicit guidance).
/// </summary>
public static class OtpRateLimitEmailCaptureMiddleware
{
    private const string CapturedEmailItemKey = "Storporate.Identity.OtpRateLimitEmail";
    private const string FallbackPartitionKey = "unknown";

    public static IApplicationBuilder UseOtpRateLimitEmailCapture(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsOtpEndpoint(context) && context.Request.ContentType?.StartsWith("application/json") == true)
            {
                context.Request.EnableBuffering();
                context.Items[CapturedEmailItemKey] = await ReadEmailFromBodyAsync(context);
                context.Request.Body.Position = 0;
            }

            await next(context);
        });

    /// <summary>Read back by <see cref="OtpRateLimiterPolicy"/>'s per-email partition-key
    /// function. Falls back to a shared "unknown" bucket (rather than throwing) when the body
    /// wasn't captured or didn't contain a usable email, so a malformed request is still subject
    /// to *some* rate limit rather than bypassing it entirely.</summary>
    internal static string ResolveCapturedEmail(HttpContext context) =>
        context.Items.TryGetValue(CapturedEmailItemKey, out var email) && email is string { Length: > 0 } emailValue
            ? emailValue
            : FallbackPartitionKey;

    private static bool IsOtpEndpoint(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api/auth/otp", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadEmailFromBodyAsync(HttpContext context)
    {
        try
        {
            using var jsonDocument = await JsonDocument.ParseAsync(context.Request.Body);
            if (jsonDocument.RootElement.TryGetProperty("email", out var emailElement)
                && emailElement.ValueKind == JsonValueKind.String)
            {
                var email = emailElement.GetString();
                return string.IsNullOrWhiteSpace(email) ? FallbackPartitionKey : email.Trim().ToLowerInvariant();
            }
        }
        catch (JsonException)
        {
            // Malformed body — fall through to the shared "unknown" partition rather than
            // failing the request here; the endpoint's own model binding/validation rejects it
            // with a proper error afterwards.
        }

        return FallbackPartitionKey;
    }
}
