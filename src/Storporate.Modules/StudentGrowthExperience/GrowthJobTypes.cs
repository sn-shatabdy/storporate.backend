namespace Storporate.Modules.StudentGrowthExperience;

/// <summary>
/// Discriminator strings for STOR-40 Phase 2 <see cref="Storporate.SharedKernel.Entities.Job"/>
/// rows owned by the StudentGrowthExperience module. Mirrors the
/// <see cref="Storporate.Modules.Portfolio.Analysis.PortfolioJobTypes"/> shape
/// (single nested static class with <c>const string</c> entries) so the two
/// modules share the same registration pattern: the worker matches a job's
/// <c>Type</c> to a registered <see cref="Storporate.Infrastructure.Jobs.IBackgroundJobProcessor"/>
/// without needing a cross-module reference.
/// </summary>
public static class GrowthJobTypes
{
    /// <summary>One advisor turn — opening, reply, or refresh — for a single
    /// <see cref="Storporate.SharedKernel.Entities.Exploration"/>. The payload
    /// carries the exploration id and the mode discriminator.</summary>
    public const string AdvisorTurn = "AdvisorTurn";

    /// <summary>Side-by-side comparison of two
    /// <see cref="Storporate.SharedKernel.Entities.Exploration"/> rows. The
    /// payload carries the
    /// <see cref="Storporate.SharedKernel.Entities.ExplorationComparison"/>
    /// id that the GET endpoint will read by.</summary>
    public const string CompareExplorations = "CompareExplorations";
}

/// <summary>
/// Mode discriminator for an <see cref="GrowthJobTypes.AdvisorTurn"/> job.
/// Persisted on the job's <c>PayloadJson</c> so the processor reads it back
/// without round-tripping the parent <see cref="Storporate.SharedKernel.Entities.Exploration"/>
/// just to learn which prompt branch to take.
/// </summary>
public static class AdvisorTurnModes
{
    /// <summary>First turn for a freshly-created exploration. The user prompt
    /// carries the student's initial direction (if any) and the system
    /// prompt instructs the model to either ask personal questions or, if the
    /// direction is already rich enough, jump straight to a summary.</summary>
    public const string Opening = "Opening";

    /// <summary>Subsequent turn after the student replied to a question or
    /// added free-text. The user prompt carries the new student message and
    /// the history list brings the prior turns along.</summary>
    public const string Reply = "Reply";

    /// <summary>Re-derive the summary from the existing messages without
    /// adding a new student turn. Used by <c>POST /explorations/{id}/refresh</c>.</summary>
    public const string Refresh = "Refresh";
}

/// <summary>Payload for an <see cref="GrowthJobTypes.AdvisorTurn"/> job.
/// Serialized into <see cref="Storporate.SharedKernel.Entities.Job.PayloadJson"/>
/// at enqueue time; deserialized by <c>AdvisorTurnJobProcessor</c> under the
/// job's account scope.</summary>
/// <param name="ExplorationId">The owning exploration's id. The processor
/// loads it under the global query filter — a cross-account id never
/// matches and the job fails fast.</param>
/// <param name="Mode">One of <see cref="AdvisorTurnModes"/>.</param>
public sealed record AdvisorTurnPayload(Guid ExplorationId, string Mode);

/// <summary>Payload for a <see cref="GrowthJobTypes.CompareExplorations"/> job.
/// Carries the comparison row's id so the processor can load it (and the two
/// participating explorations) under the job's account scope.</summary>
/// <param name="ComparisonId">The owning
/// <see cref="Storporate.SharedKernel.Entities.ExplorationComparison"/> id.</param>
public sealed record CompareExplorationsPayload(Guid ComparisonId);
