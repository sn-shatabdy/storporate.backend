using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Storage;
using Storporate.Modules.Identity.Exceptions;

namespace Storporate.Api.Errors;

/// <summary>
/// The single place unhandled exceptions become HTTP responses. Every failure — validation or
/// otherwise — comes back as the same two-field <see cref="ErrorResponse"/> shape, so API
/// consumers never have to branch on error format.
/// </summary>
public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, response) = exception switch
        {
            ValidationException validationException =>
                (StatusCodes.Status400BadRequest, MapValidationError(validationException)),
            LlmProviderException llmProviderException =>
                (StatusCodes.Status502BadGateway, MapLlmProviderError(llmProviderException)),
            ArtifactStorageException artifactStorageException =>
                (StatusCodes.Status502BadGateway, MapArtifactStorageError(artifactStorageException)),
            OtpInvalidException otpInvalidException =>
                (StatusCodes.Status400BadRequest, new ErrorResponse("otp_invalid", otpInvalidException.Message)),
            OtpLockedException otpLockedException =>
                (StatusCodes.Status401Unauthorized, new ErrorResponse("otp_locked", otpLockedException.Message)),
            OtpRateLimitExceededException otpRateLimitExceededException =>
                (StatusCodes.Status429TooManyRequests, new ErrorResponse("otp_rate_limit_exceeded", otpRateLimitExceededException.Message)),
            EmailAlreadyRegisteredException emailAlreadyRegisteredException =>
                (StatusCodes.Status409Conflict, new ErrorResponse("email_already_registered", emailAlreadyRegisteredException.Message)),
            RefreshTokenReusedException refreshTokenReusedException =>
                (StatusCodes.Status401Unauthorized, new ErrorResponse("refresh_token_reused", refreshTokenReusedException.Message)),
            _ => (StatusCodes.Status500InternalServerError, MapUnhandledError(exception)),
        };

        if (statusCode is StatusCodes.Status500InternalServerError or StatusCodes.Status502BadGateway)
        {
            logger.LogError(
                exception,
                "Unhandled exception while processing {Method} {Path}",
                httpContext.Request.Method,
                httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);
        return true;
    }

    private static ErrorResponse MapValidationError(ValidationException exception)
    {
        var firstFailure = exception.Errors.First();
        var errorCode = string.IsNullOrWhiteSpace(firstFailure.ErrorCode)
            ? "validation_error"
            : firstFailure.ErrorCode;

        return new ErrorResponse(errorCode, firstFailure.ErrorMessage);
    }

    private ErrorResponse MapLlmProviderError(LlmProviderException exception) =>
        environment.IsDevelopment()
            ? new ErrorResponse("llm_provider_error", exception.Message)
            : new ErrorResponse("llm_provider_error", "The AI provider is currently unavailable. Please try again later.");

    private ErrorResponse MapArtifactStorageError(ArtifactStorageException exception) =>
        environment.IsDevelopment()
            ? new ErrorResponse("artifact_storage_error", exception.Message)
            : new ErrorResponse("artifact_storage_error", "File storage is currently unavailable. Please try again later.");

    private ErrorResponse MapUnhandledError(Exception exception) =>
        environment.IsDevelopment()
            ? new ErrorResponse("unhandled_exception", exception.ToString())
            : new ErrorResponse("unhandled_exception", "An unexpected error occurred. Please try again later.");
}
