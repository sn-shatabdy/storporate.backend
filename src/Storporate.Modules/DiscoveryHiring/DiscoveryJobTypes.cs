namespace Storporate.Modules.DiscoveryHiring;

/// <summary>
/// Discriminator strings for STOR-43 Phase 2 <see cref="Storporate.SharedKernel.Entities.Job"/>
/// rows owned by the DiscoveryHiring module. Lives next to the Phase 1
/// <see cref="Storporate.SharedKernel.TalentIndexJobTypes"/> (which is in
/// SharedKernel because Portfolio also enqueues refresh jobs) — Phase 2's
/// <c>SearchTalent</c> job is only enqueued by the DiscoveryHiring module's
/// own endpoint, so it does not need to be in SharedKernel and the module
/// stays self-contained.
/// </summary>
public static class DiscoveryJobTypes
{
    /// <summary>One plain-language employer talent search. The payload
    /// carries the <see cref="Storporate.SharedKernel.Entities.TalentSearchRequest"/>
    /// id that the GET endpoint will read by.</summary>
    public const string SearchTalent = "SearchTalent";
}

/// <summary>Payload for a <see cref="DiscoveryJobTypes.SearchTalent"/> job.
/// Serialized into <see cref="Storporate.SharedKernel.Entities.Job.PayloadJson"/>
/// at enqueue time; deserialized by <c>SearchTalentJobProcessor</c> under
/// the job's account scope.</summary>
/// <param name="SearchId">The owning
/// <see cref="Storporate.SharedKernel.Entities.TalentSearchRequest"/> id.</param>
public sealed record SearchTalentPayload(Guid SearchId);
