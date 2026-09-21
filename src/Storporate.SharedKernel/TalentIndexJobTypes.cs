namespace Storporate.SharedKernel;

/// <summary>
/// Discriminator strings for STOR-43 Phase 1 <see cref="Entities.Job"/> rows
/// owned by the DiscoveryHiring module. Mirrors the
/// <see cref="Storporate.Modules.StudentGrowthExperience.GrowthJobTypes"/> shape
/// (single nested static class with <c>const string</c> entries) so the
/// polling worker matches a job's <c>Type</c> to a registered
/// <see cref="Storporate.Infrastructure.Jobs.IBackgroundJobProcessor"/> without
/// any module-to-module reference. Lives in SharedKernel (not in the
/// DiscoveryHiring module) because the Portfolio module also references these
/// strings when it enqueues a refresh job after a successful analysis or a
/// portfolio-item deletion — the same sibling-module isolation rule that
/// already drives <see cref="Entities.Job.PayloadJson"/> to round-trip a
/// serialized payload instead of a cross-module type.
/// </summary>
public static class TalentIndexJobTypes
{
    /// <summary>Rebuild the <see cref="Entities.TalentIndexEntry"/> projection
    /// for a single opted-in student. Triggered by the
    /// <c>PUT /api/discovery/searchable-profile</c> endpoint (when the student
    /// is searchable), by every successful portfolio analysis
    /// (<c>PortfolioAnalysisJobProcessor</c>), and by every portfolio-item
    /// deletion (<c>DeletePortfolioItemHandler</c>).</summary>
    public const string RefreshEntry = "RefreshTalentIndexEntry";
}

/// <summary>
/// Payload for a <see cref="TalentIndexJobTypes.RefreshEntry"/> job.
/// Carries the student's <see cref="Entities.User.Id"/> so the processor can
/// load the <see cref="Entities.StudentSearchProfile"/> + analyzed portfolio
/// under the account scope.
/// </summary>
/// <param name="StudentAccountId">The owning student's id.</param>
public sealed record RefreshTalentIndexPayload(Guid StudentAccountId);
