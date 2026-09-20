namespace Storporate.SharedKernel.Entities;

/// <summary>
/// Lifecycle status of a <see cref="TalentSearchRequest"/>. Mirrors the
/// <see cref="ExplorationComparisonStatuses"/> shape so the two polling
/// endpoints behave identically from the FE's perspective.
/// </summary>
public static class TalentSearchStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
