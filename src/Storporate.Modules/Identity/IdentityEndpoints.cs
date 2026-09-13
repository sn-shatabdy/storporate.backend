using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Security;
using Storporate.Infrastructure.Security.RateLimiting;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Modules.Identity;

/// <summary>
/// New endpoint-mapping pattern for STOR-61: unlike every existing endpoint (mapped inline in
/// <c>Program.cs</c>), the Identity module's endpoints are grouped in their own extension method,
/// called once from <c>Program.cs</c> — a deliberate, cleaner pattern for this module going
/// forward, not an oversight.
/// </summary>
public static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/otp/request", async (
                RequestOtpRequest request,
                IValidator<RequestOtpRequest> validator,
                WriteDbContext dbContext,
                IEmailSender emailSender,
                IOptions<OtpOptions> otpOptions,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken);

                await RequestOtpHandler.ExecuteAsync(request.Email, dbContext, emailSender, otpOptions, cancellationToken);

                // Identical body/status regardless of whether `request.Email` has an account —
                // see RequestOtpHandler's no-account-enumeration doc comment.
                return Results.Ok(new RequestOtpResponse("If this email is valid, a verification code has been sent."));
            })
            .RequireRateLimiting(OtpRateLimiterPolicy.PolicyName);

        app.MapPost("/api/auth/otp/verify", async (
                VerifyOtpRequest request,
                IValidator<VerifyOtpRequest> validator,
                HttpContext httpContext,
                WriteDbContext dbContext,
                IJwtTokenService tokenService,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken);

                var userAgent = httpContext.Request.Headers.UserAgent.ToString();
                var result = await VerifyOtpHandler.ExecuteAsync(
                    request.Email,
                    request.Code,
                    request.ActorType,
                    string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
                    dbContext,
                    tokenService,
                    cancellationToken);

                return Results.Ok(new VerifyOtpResponse(
                    result.User.Id,
                    result.User.Email,
                    result.User.ActorType,
                    result.User.VerificationStatus,
                    result.IsNewUser,
                    result.Tokens.AccessToken,
                    result.Tokens.AccessTokenExpiresAt,
                    result.Tokens.RefreshToken,
                    result.Tokens.RefreshTokenExpiresAt));
            })
            .RequireRateLimiting(OtpRateLimiterPolicy.PolicyName);
    }
}
