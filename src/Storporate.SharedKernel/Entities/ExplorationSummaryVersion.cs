namespace Storporate.SharedKernel.Entities;

/// <summary>
/// An immutable snapshot of an <see cref="Exploration"/>'s gaps and
/// suggestions at a point in time. A new version is appended on every
/// successful advisor turn (and on every successful refresh); the
/// <see cref="VersionNumber"/> is monotonic per <see cref="ExplorationId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both <see cref="GapsJson"/> and <see cref="SuggestionsJson"/> stay as JSON
/// blobs because the gap / suggestion shapes are free-form, always read
/// together, and never queried individually — exactly the storage shape the
/// plan calls out. <see cref="ChangeNote"/> is the human-readable "what
/// changed since the last version" line surfaced in the summary panel; null
/// on the very first version.
/// </para>
/// </remarks>
public sealed class ExplorationSummaryVersion : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public User? Account { get; set; }

    public Guid ExplorationId { get; set; }

    public Exploration? Exploration { get; set; }

    /// <summary>Monotonic per <see cref="ExplorationId"/>, starting at 1.
    /// The advisor pipeline computes <c>MAX(VersionNumber) + 1</c> per
    /// exploration at version-write time; the application-side concurrency
    /// story is bounded by the per-exploration single-active-turn rule, so
    /// no race is possible.</summary>
    public int VersionNumber { get; set; }

    /// <summary>JSON-encoded gaps array (see the plan's AI contract:
    /// <c>gaps[{title, detail, band?}]</c>).</summary>
    public required string GapsJson { get; set; }

    /// <summary>JSON-encoded suggestions array (see the plan's AI contract:
    /// <c>suggestions[{title, reason, nextStep, sourceItemId?}]</c>).</summary>
    public required string SuggestionsJson { get; set; }

    /// <summary>Free-text "what changed since the last version" note.
    /// Null on the first version of an exploration.</summary>
    public string? ChangeNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}