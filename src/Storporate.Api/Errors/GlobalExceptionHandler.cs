using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Storage;
using Storporate.Modules.DiscoveryHiring.Exceptions;
using Storporate.Modules.Identity.Exceptions;
using Storporate.Modules.SecurityGovernance;

namespace Storporate.Api.Errors;

/// <summary>
/// The single place unhandled exceptions become HTTP responses. Every failure — validation or
/// otherwise — comes back as the same two-field <see cref="ErrorResponse"/> shape, so API
/// consumers never have to branch on error format.
/// </summary>
/// <remarks>
/// The 400 "file_too_large" arm fires when <see cref="System.IO.InvalidDataException"/> is
/// raised by the multipart reader after the request body crosses
/// <c>FormOptions.MultipartBodyLengthLimit</c>. The validator would catch the same case if
/// the body were within the multipart limit, so mapping to the same error code keeps the
/// client's contract uniform regardless of which limit fired first.
/// </remarks>
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
            RefreshTokenInvalidException refreshTokenInvalidException =>
                (StatusCodes.Status401Unauthorized, new ErrorResponse("refresh_token_invalid", refreshTokenInvalidException.Message)),
            GoogleLoginFailedException googleLoginFailedException =>
                (StatusCodes.Status401Unauthorized, new ErrorResponse("google_login_failed", googleLoginFailedException.Message)),
            GoogleEmailNotVerifiedException googleEmailNotVerifiedException =>
                (StatusCodes.Status409Conflict, new ErrorResponse("google_email_not_verified", googleEmailNotVerifiedException.Message)),
            Storporate.Modules.Portfolio.PortfolioAnalysisNotRetryableException portfolioAnalysisNotRetryableException =>
                (StatusCodes.Status409Conflict, new ErrorResponse("portfolio_analysis_not_retryable", portfolioAnalysisNotRetryableException.Message)),
            Storporate.Modules.StudentGrowthExperience.Exceptions.ExplorationBusyException explorationBusyException =>
                (StatusCodes.Status409Conflict, new ErrorResponse("exploration_busy", explorationBusyException.Message)),
            Storporate.Modules.DiscoveryHiring.Exceptions.TalentSearchBusyException talentSearchBusyException =>
                (StatusCodes.Status409Conflict, new ErrorResponse("talent_search_busy", talentSearchBusyException.Message)),
            Storporate.Modules.DiscoveryHiring.Exceptions.JobPostingClosedException jobPostingClosedException =>
                (StatusCodes.Status409Conflict, new ErrorResponse("job_posting_closed", jobPostingClosedException.Message)),
            Storporate.Modules.StudentGrowthExperience.Exceptions.ExplorationNotRetryableException explorationNotRetryableException =>
                (StatusCodes.Status409Conflict, new ErrorResponse("exploration_not_retryable", explorationNotRetryableException.Message)),
            Storporate.Modules.StudentGrowthExperience.Exceptions.ExplorationLimitReachedException explorationLimitReachedException =>
                (StatusCodes.Status409Conflict, new ErrorResponse("exploration_limit_reached", explorationLimitReachedException.Message)),
            Storporate.Modules.StudentGrowthExperience.Exceptions.ExplorationHasNoSummaryException explorationHasNoSummaryException =>
                (StatusCodes.Status409Conflict, new ErrorResponse("exploration_has_no_summary", explorationHasNoSummaryException.Message)),
            ActorTypeRequiredException actorTypeRequiredException =>
                (StatusCodes.Status400BadRequest, new ErrorResponse("actor_type_required", actorTypeRequiredException.Message)),
            UnknownSortKeyException unknownSortKeyException =>
                (StatusCodes.Status400BadRequest, new ErrorResponse("unknown_sort_key", unknownSortKeyException.Message)),
            Storporate.Modules.Portfolio.UnknownSortKeyException portfolioUnknownSortKey =>
                (StatusCodes.Status400BadRequest, new ErrorResponse("unknown_sort_key", portfolioUnknownSortKey.Message)),
            // The multipart reader throws InvalidDataException once the request body
            // crosses FormOptions.MultipartBodyLengthLimit — see Program.cs's
            // Configure<FormOptions>. Map it to a clean 400 / file_too_large rather
            // than a 500 so the client gets the same error code they'd get from the
            // validator if the body had been inside the multipart limit.
            InvalidDataException invalidDataException when
                invalidDataException.Message.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase) =>
                (StatusCodes.Status400BadRequest, new ErrorResponse("file_too_large", invalidDataException.Message)),
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
