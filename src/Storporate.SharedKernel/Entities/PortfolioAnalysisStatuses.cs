namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The allowed values for <see cref="PortfolioItem.AnalysisStatus"/> — the per-item
/// state machine driven by the STOR-38 background worker (Phase 2). Kept as string
/// constants (not an enum), mirroring <see cref="JobStatus"/>, <see cref="PortfolioCategories"/>,
/// and <see cref="VerificationStatuses"/> so the database column stays a readable string and
/// adding a new terminal state later doesn't require a migration to widen a numeric range.
/// </summary>
/// <remarks>
/// <para>
/// <b>State flow.</b> A newly created item lands in <see cref="NotAnalyzed"/>; the worker
/// flips it to <see cref="Analyzing"/> while a job is in flight; <see cref="Analyzed"/> is
/// the happy terminal state once <see cref="PortfolioSkillFinding"/> rows have been written.
/// </para>
/// <para>
/// <b>Failed vs. Unsupported.</b> <see cref="Failed"/> means "the analysis ran and couldn't
/// produce a useful result — the user can retry". <see cref="Unsupported"/> means "we never
/// even tried, because the evidence type has no extractable text (e.g. raw video, image,
/// design-tool binary)" — retrying can never succeed, so the UI hides the Retry button for
/// <see cref="Unsupported"/> rows. The distinction drives which items surface a Retry
/// affordance in the portfolio list and detail page.
/// </para>
/// </remarks>
public static class PortfolioAnalysisStatuses
{
    /// <summary>Initial state for a freshly-submitted <see cref="PortfolioItem"/>; no
    /// analysis job has run yet (or one has been queued but not yet claimed).</summary>
    public const string NotAnalyzed = "NotAnalyzed";

    /// <summary>A worker has claimed the item's analysis job and is currently running
    /// <c>ILlmClient.CompleteAsync</c>.</summary>
    public const string Analyzing = "Analyzing";

    /// <summary>Terminal happy state: the worker wrote one or more
    /// <see cref="PortfolioSkillFinding"/> rows for the item.</summary>
    public const string Analyzed = "Analyzed";

    /// <summary>Terminal failure state: the worker hit <see cref="JobStatus.Failed"/>
    /// after exhausting its retry budget. A new "Retry" action (Phase 3) flips the item
    /// back to <see cref="NotAnalyzed"/> and queues a fresh job.</summary>
    public const string Failed = "Failed";

    /// <summary>Terminal unrecoverable state: the evidence type (e.g. raw video, image,
    /// binary design file) has no extractable text path so no LLM call was ever made.
    /// Distinct from <see cref="Failed"/> so the UI can hide the Retry affordance.</summary>
    public const string Unsupported = "Unsupported";
}
