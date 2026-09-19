namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A side-by-side comparison of two of the student's Explorations. Created
/// by <c>POST /explorations/compare</c> and populated by the Phase 3
/// <c>CompareExplorationsJobProcessor</c>; read by
/// <c>GET /explorations/compare/{id}</c>.
/// </summary>
public sealed class ExplorationComparison : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public User? Account { get; set; }

    /// <summary>The first exploration in the compare pair. Required.</summary>
    public Guid FirstExplorationId { get; set; }

    public Exploration? FirstExploration { get; set; }

    /// <summary>The second exploration in the compare pair. Required. The
    /// compare endpoint rejects a request where <see cref="FirstExplorationId"/>
    /// equals <see cref="SecondExplorationId"/>.</summary>
    public Guid SecondExplorationId { get; set; }

    public Exploration? SecondExploration { get; set; }

    /// <summary>One of <see cref="ExplorationComparisonStatuses"/>.</summary>
    public string Status { get; set; } = ExplorationComparisonStatuses.Pending;

    /// <summary>The free-text comparison body produced by the compare job.
    /// Null until the job reaches <see cref="ExplorationComparisonStatuses.Completed"/>.</summary>
    public string? ResultText { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}