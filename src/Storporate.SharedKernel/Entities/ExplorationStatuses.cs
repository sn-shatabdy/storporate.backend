namespace Storporate.SharedKernel.Entities;

/// <summary>
/// Lifecycle status of a <see cref="Exploration"/>. The string values are
/// persisted to the database and round-trip through the EF Core mapping on
/// <c>Explorations.Status</c>, so changing them is a breaking schema change
/// (see the plan's "Status field" guidance).
/// </summary>
public static class ExplorationStatuses
{
    /// <summary>The exploration has no in-flight work and is ready to accept
    /// a new turn. The Phase 2 worker flips the status here after writing a
    /// successful turn, and the retry endpoint also lands here briefly before
    /// re-enqueueing.</summary>
    public const string Idle = "Idle";

    /// <summary>An <c>AdvisorTurn</c> job has been claimed for this exploration
    /// and is in flight. The Phase 2 handler rejects new turns with
    /// <c>409 exploration_busy</c> while in this state.</summary>
    public const string Working = "Working";

    /// <summary>The most recent turn exhausted its retry budget (3 attempts).
    /// A user-initiated retry moves the exploration back to <c>Working</c> on
    /// the next enqueue.</summary>
    public const string Failed = "Failed";
}