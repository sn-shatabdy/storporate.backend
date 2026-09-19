using System.ComponentModel.DataAnnotations;

namespace Storporate.Infrastructure.Jobs;

/// <summary>
/// Bound options for the polling background worker and the stale-running reaper.
/// STOR-40 Phase 1: the reaper relies on <see cref="StaleRunningAfter"/>; the worker
/// itself still uses the hard-coded <see cref="PortfolioAnalysisWorker.PollInterval"/>
/// for the polling cadence (a story that needs to tune the cadence can move that
/// knob here without changing call sites).
/// </summary>
public sealed class BackgroundJobOptions
{
    public const string SectionName = "BackgroundJobs";

    /// <summary>
    /// A <see cref="SharedKernel.Entities.Job"/> left in <c>Running</c> state longer
    /// than this window is treated as abandoned by the reaper: its <c>AttemptCount</c>
    /// is incremented and its status is reset to <c>Pending</c> (or, on hitting
    /// <see cref="SharedKernel.Entities.MaxAttemptsConstant"/>, set to <c>Failed</c>).
    /// Default 10 minutes — generous enough that a slow LLM response doesn't get
    /// preempted, tight enough that a dead worker doesn't keep a job in limbo for hours.
    /// Not decorated with <c>[Required]</c> so test hosts that bind
    /// <see cref="SectionName"/> with no value are unaffected (matches the project
    /// rule established when <c>PermissionCoverageTests</c>'s placeholders were
    /// written: every <c>[Required]</c> on an option must also be added to those
    /// hosts).
    /// </summary>
    public TimeSpan StaleRunningAfter { get; init; } = TimeSpan.FromMinutes(10);
}
