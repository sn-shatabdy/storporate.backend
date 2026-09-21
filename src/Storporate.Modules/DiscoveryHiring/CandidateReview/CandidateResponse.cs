namespace Storporate.Modules.DiscoveryHiring.CandidateReview;

/// <summary>
/// Wire-shape DTOs for <c>GET /api/discovery/candidates/{candidateId}</c>. The
/// handler reads only the non-tenant <see cref="Storporate.SharedKernel.Entities.TalentIndexEntry"/>
/// table — Organizations cannot read student-private tables
/// (<see cref="Storporate.SharedKernel.Entities.PortfolioItem"/>,
/// <see cref="Storporate.SharedKernel.Entities.StudentSearchProfile"/>) because
/// RLS blocks cross-account reads, so every field here is derived from the index
/// snapshot the refresh processor copies into <c>ItemsJson</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Profile fields come from the entry.</b>
/// <see cref="DisplayName"/>, <see cref="Headline"/>, <see cref="University"/>,
/// <see cref="FieldOfStudy"/>, and <see cref="StudyYear"/> mirror the
/// student-controlled values the processor stored at refresh time. The
/// processor already nulls out the four Show-flag-controlled fields when the
/// student hid them, so a non-null here always means "the student chose to
/// show this" and a null means either "no value" or "student hid it" — the
/// FE never has to differentiate.
/// </para>
/// <para>
/// <b>Per-item shape is "shared" OR not.</b>
/// <see cref="CandidateItemResponse.Shared"/> is the contract: an item is
/// shared iff its snapshot carries a non-null <c>original</c> descriptor
/// (the only way the refresh processor copied a storage key / url into the
/// non-tenant index — see STOR-44 Phase 1 in
/// <see cref="Storporate.SharedKernel.Entities.TalentIndexEntry"/>) AND the
/// descriptor is well-formed (a File submission with a storage key, or a Link
/// submission with an http/https URL the handler could parse). When
/// <see cref="CandidateItemResponse.Shared"/> is <see langword="false"/> the
/// <c>skills</c> array still surfaces the per-skill band so the FE can render
/// the item row but <c>reason</c> and <c>original</c> are deliberately null —
/// AI reasoning and the original stream belong to the shared surface only,
/// per the plan's data-rules section.
/// </para>
/// <para>
/// <b>What is never returned.</b>
/// Storage keys, full URLs, account ids (owning or any other), emails,
/// scores, ranks, ratings, percentages — the response-shape guard test pins
/// this contract literally against the rendered JSON.
/// </para>
/// </remarks>
public sealed record CandidateResponse(
    Guid CandidateId,
    string DisplayName,
    string? Headline,
    string? University,
    string? FieldOfStudy,
    int? StudyYear,
    IReadOnlyList<CandidateItemResponse> Items);

/// <summary>One portfolio item behind a <see cref="CandidateResponse"/>. Items
/// appear in the same order the processor wrote them into
/// <see cref="Storporate.SharedKernel.Entities.TalentIndexEntry.ItemsJson"/>.</summary>
public sealed record CandidateItemResponse(
    Guid PortfolioItemId,
    string Label,
    string Category,
    bool Shared,
    IReadOnlyList<CandidateSkillResponse> Skills,
    CandidateOriginalResponse? Original);

/// <summary>One skill finding on a <see cref="CandidateItemResponse"/>.
/// <see cref="Band"/> is always one of <c>Strong</c> or <c>Developing</c>
/// (never <c>Missing</c>) because the snapshot excludes Missing-band findings
/// at refresh time. <see cref="Reason"/> is the AI-written short explanation
/// for the band, returned only when the parent item is shared.</summary>
public sealed record CandidateSkillResponse(string Name, string Band, string? Reason);

/// <summary>Wire shape for the per-item "where is the original?" descriptor an
/// Organization can drill down behind. <see cref="Kind"/> is always one of
/// <see cref="Storporate.SharedKernel.Entities.TalentIndexOriginalKinds.File"/>
/// or <c>Link</c>; the other fields are populated conditionally on the kind.
/// <see cref="Available"/> mirrors the original-source sanity check: a Link
/// whose <c>Url</c> cannot be parsed to http/https, or a File whose stored
/// URL / storage key is missing, comes through as
/// <c>available:false</c> so the FE can hide the open button without
/// reaching for a second endpoint. The endpoint's 404 contract collapses
/// every "unavailable" case onto the same errorCode so the FE does not get
/// signal about which items exist behind the index.</summary>
public sealed record CandidateOriginalResponse(
    string Kind,
    bool Available,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    string? Host);
