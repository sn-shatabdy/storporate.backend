namespace Storporate.Modules.DiscoveryHiring.TalentSearch;

/// <summary>
/// Wire-shape DTOs for the talent-search endpoints. <see cref="TalentSearchResponse"/>
/// is what <c>GET /api/discovery/talent-searches/{id}</c> returns;
/// <see cref="TalentSearchAcceptedResponse"/> is what the POST handler returns
/// inline at <c>Results.Accepted(value: new { searchId })</c>.
/// </summary>
/// <remarks>
/// All wire shapes here are flat records with primitive fields so the JSON
/// projection is the same shape the FE expects (camelCase via
/// <c>JsonSerializerDefaults.Web</c>). The result item shape mirrors the
/// <see cref="Storporate.SharedKernel.Entities.TalentSearchRequest.ResultJson"/>
/// payload the processor writes, with two additions: the GET handler
/// re-validates each candidate against the live index so an item the
/// student hid since the search ran is dropped before the response goes
/// out the door. No field here ever carries a numeric score, a percentage,
/// a rank number, a rating, an email, an account id, a storage key, or a
/// file name — the response-shape guard test enforces that.
/// </remarks>
public sealed record TalentSearchResponse(
    Guid Id,
    string Status,
    string Query,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<TalentSearchResultItem>? Results,
    string? ErrorCode);

/// <summary>Response body for <c>POST /api/discovery/talent-searches</c>.
/// Carries only the new search id; the FE polls the GET endpoint to
/// observe status / results.</summary>
public sealed record TalentSearchAcceptedResponse(Guid SearchId);

/// <summary>One ranked (or unranked-tail) student in a Completed
/// <see cref="TalentSearchResponse.Results"/> array. Profile fields are
/// read from the live <c>TalentIndexEntry</c> on every GET — the student
/// may have hidden a field since the search ran, and the FE must never
/// see a value the student hid.</summary>
public sealed record TalentSearchResultItem(
    Guid CandidateId,
    string DisplayName,
    string? Headline,
    string? University,
    string? FieldOfStudy,
    int? StudyYear,
    IReadOnlyList<TalentSearchMatchedSkill> MatchedSkills,
    string Reason,
    IReadOnlyList<TalentSearchCitedItem> CitedItems);

/// <summary>One matched skill on a <see cref="TalentSearchResultItem"/>.
/// <see cref="Band"/> is always one of <c>Strong</c> or
/// <c>Developing</c> (never <c>Missing</c>) because the index excludes
/// Missing-band findings — see <c>RefreshTalentIndexEntryProcessor</c>.</summary>
public sealed record TalentSearchMatchedSkill(string Name, string Band);

/// <summary>One cited portfolio item backing a result item's reason.
/// <see cref="SkillName"/> and <see cref="Band"/> reflect the live
/// <c>TalentIndexEntry</c>'s snapshot; <see cref="Label"/> and
/// <see cref="Category"/> are refreshed on every GET so a student who
/// edited their item since the search ran is still cited correctly.</summary>
public sealed record TalentSearchCitedItem(
    Guid PortfolioItemId,
    string Label,
    string Category,
    string SkillName,
    string Band);
