namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A single advisor conversation. An Exploration is the unit of "I'm exploring
/// this direction" — its messages, its live summary, its gap/suggestion lists —
/// and is owned end-to-end by the student who created it. The advisor pipeline
/// only ever acts on one Exploration at a time per account (the Phase 2
/// handler enforces <c>409 exploration_busy</c> while <see cref="Status"/> is
/// <see cref="ExplorationStatuses.Working"/>).
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IAccountScoped"/> so the global query filter, save-time
/// interceptor, and Postgres RLS policy added by Phase 1's
/// <c>AddStudentGrowthRowLevelSecurity</c> migration cover this table with
/// the same three-layer isolation as <see cref="PortfolioItem"/>.
/// </para>
/// </remarks>
public sealed class Exploration : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public User? Account { get; set; }

    /// <summary>The student's chosen title (or the auto-generated one for an
    /// opening turn). Editable via <c>PUT /explorations/{id}/title</c>.</summary>
    public required string Title { get; set; }

    /// <summary>One of <see cref="ExplorationStatuses"/>. Defaults to
    /// <see cref="ExplorationStatuses.Idle"/> so a freshly-created row is
    /// immediately ready for an opening turn without an application-side
    /// write.</summary>
    public string Status { get; set; } = ExplorationStatuses.Idle;

    /// <summary>Most-recent worker error message. Populated when
    /// <see cref="Status"/> flips to <see cref="ExplorationStatuses.Failed"/>
    /// and cleared on the next successful turn. Surfaced by the per-exploration
    /// read endpoint but never sent in audit metadata.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}