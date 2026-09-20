namespace Storporate.SharedKernel.Entities;

/// <summary>
/// Lifecycle status of an <see cref="ExplorationComparison"/>. The Phase 3
/// compare job moves rows from <see cref="Pending"/> to
/// <see cref="Completed"/> or <see cref="Failed"/>; the comparison endpoint
/// only returns rows that have reached <see cref="Completed"/>.
/// </summary>
public static class ExplorationComparisonStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}