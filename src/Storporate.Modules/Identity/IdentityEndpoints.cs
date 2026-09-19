using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Auditing;
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
                IAuditLogWriter auditLogWriter,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken);

                await RequestOtpHandler.ExecuteAsync(request.Email, dbContext, emailSender, otpOptions, auditLogWriter, cancellationToken);

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
                IAuditLogWriter auditLogWriter,
                IOptions<OtpOptions> otpOptions,
                IHostEnvironment environment,
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
                    auditLogWriter,
                    otpOptions,
                    environment,
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

        app.MapPost("/api/auth/google", async (
                GoogleLoginRequest request,
                IValidator<GoogleLoginRequest> validator,
                HttpContext httpContext,
                WriteDbContext dbContext,
                IGoogleIdTokenValidator googleIdTokenValidator,
                IJwtTokenService tokenService,
                IAuditLogWriter auditLogWriter,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken);

                var userAgent = httpContext.Request.Headers.UserAgent.ToString();
                var result = await GoogleLoginHandler.ExecuteAsync(
                    request.IdToken,
                    request.ActorType,
                    string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
                    dbContext,
                    googleIdTokenValidator,
                    tokenService,
                    auditLogWriter,
                    cancellationToken);

                return Results.Ok(new GoogleLoginResponse(
                    result.User.Id,
                    result.User.Email,
                    result.User.ActorType,
                    result.User.VerificationStatus,
                    result.IsNewUser,
                    result.Tokens.AccessToken,
                    result.Tokens.AccessTokenExpiresAt,
                    result.Tokens.RefreshToken,
                    result.Tokens.RefreshTokenExpiresAt));
            });

        app.MapPost("/api/auth/refresh", async (
                RefreshSessionRequest request,
                IValidator<RefreshSessionRequest> validator,
                HttpContext httpContext,
                WriteDbContext dbContext,
                IJwtTokenService tokenService,
                IAuditLogWriter auditLogWriter,
                CancellationToken cancellationToken) =>
            {
                await validator.ValidateAndThrowAsync(request, cancellationToken);

                var userAgent = httpContext.Request.Headers.UserAgent.ToString();
                var tokens = await RefreshSessionHandler.ExecuteAsync(
                    request.RefreshToken,
                    string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
                    dbContext,
                    tokenService,
                    auditLogWriter,
                    cancellationToken);

                return Results.Ok(new RefreshSessionResponse(
                    tokens.AccessToken,
                    tokens.AccessTokenExpiresAt,
                    tokens.RefreshToken,
                    tokens.RefreshTokenExpiresAt));
            });

        app.MapPost("/api/auth/logout", async (
                ClaimsPrincipal caller,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                CancellationToken cancellationToken) =>
            {
                await LogoutHandler.ExecuteAsync(caller, dbContext, auditLogWriter, cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization();

        app.MapPost("/api/auth/logout-all", async (
                ClaimsPrincipal caller,
                WriteDbContext dbContext,
                IAuditLogWriter auditLogWriter,
                CancellationToken cancellationToken) =>
            {
                var revokedCount = await LogoutAllHandler.ExecuteAsync(caller, dbContext, auditLogWriter, cancellationToken);
                return Results.Ok(new { revokedSessions = revokedCount });
            })
            .RequireAuthorization();

        app.MapGet("/api/auth/me", (ClaimsPrincipal caller) =>
            {
                var response = GetCurrentUserHandler.Execute(caller);
                return Results.Ok(response);
            })
            .RequireAuthorization();

        app.MapGet("/api/auth/sessions", async (
                ClaimsPrincipal caller,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var response = await ListSessionsHandler.ExecuteAsync(caller, dbContext, cancellationToken);
                return Results.Ok(response);
            })
            .RequireAuthorization();
    }
}
