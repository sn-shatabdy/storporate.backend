using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// A <c>POST /api/portfolio/items/{id}/analysis/retry</c> request was rejected because
/// the target item is not in a retryable state. The retry endpoint is only valid when
/// <see cref="PortfolioItem.AnalysisStatus"/> equals <see cref="PortfolioAnalysisStatuses.Failed"/>;
/// every other status is a no-op or actively in flight, so the call returns a 409 with
/// an explanation rather than silently re-queuing a job that the worker is about to
/// process anyway (or would overwrite the latest <c>Analyzed</c> snapshot).
/// </summary>
/// <remarks>
/// <para>
/// Mapped by <c>GlobalExceptionHandler</c> to <c>409 Conflict</c> with
/// <c>errorCode = "portfolio_analysis_not_retryable"</c> — following the
/// exception-class / status-code pattern the Identity module's <see cref="Modules.Identity.Exceptions.EmailAlreadyRegisteredException"/>
/// already establishes (see <c>GlobalExceptionHandler.TryHandleAsync</c>).
/// </para>
/// <para>
/// The message format is intentionally human-readable: the Phase 5 frontend surfaces it
/// verbatim on the Retry button's "this is why you can't retry right now" tooltip.
/// </para>
/// </remarks>
public sealed class PortfolioAnalysisNotRetryableException : Exception
{
    /// <summary>The actual <see cref="PortfolioItem.AnalysisStatus"/> value that blocked
    /// the retry. Surfaced so the global handler can include it in the response if a
    /// future iteration of the API wants a structured payload (today it's only used in
    /// the human-readable message).</summary>
    public string ActualStatus { get; }

    public PortfolioAnalysisNotRetryableException(string actualStatus)
        : base(BuildMessage(actualStatus))
    {
        ActualStatus = actualStatus;
    }

    /// <summary>Builds the human-readable message that the response body surfaces
    /// verbatim. Kept here rather than inlined into the constructor so a future
    /// localization pass can swap the string without touching the call site.</summary>
    private static string BuildMessage(string actualStatus) =>
        actualStatus switch
        {
            PortfolioAnalysisStatuses.NotAnalyzed =>
                "Analysis is queued but has not run yet; there is nothing to retry.",
            PortfolioAnalysisStatuses.Analyzing =>
                "Analysis is currently in progress; wait for it to finish or fail before retrying.",
            PortfolioAnalysisStatuses.Analyzed =>
                "Analysis already completed successfully; there is nothing to retry.",
            PortfolioAnalysisStatuses.Unsupported =>
                "This file type isn't supported by the analyzer yet (for example, video, audio, image, or raw design-tool files). Retrying won't help.",
            _ =>
                $"Cannot retry analysis in status '{actualStatus}'.",
        };
}
