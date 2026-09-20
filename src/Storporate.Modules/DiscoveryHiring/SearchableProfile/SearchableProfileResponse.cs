namespace Storporate.Modules.DiscoveryHiring.SearchableProfile;

/// <summary>
/// The wire-level shape returned by <c>GET /api/discovery/searchable-profile</c>
/// and <c>PUT /api/discovery/searchable-profile</c>. Mirrors the request
/// shape (so a PUT-then-GET round-trip yields identical values) plus the
/// server-computed <see cref="VisibleItemCount"/> that the Phase 3 page
/// renders under the preview card.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every input field is echoed on the response.</b> The student
/// toggled the show flags; the FE needs the resolved values to render
/// the preview card without a second round-trip. Returning the input
/// verbatim also makes the PUT response confirm what the server stored.
/// </para>
/// <para>
/// <b>Why <see cref="VisibleItemCount"/> is on the GET response only.</b>
/// Computing it on every PUT would mean running a second SQL count query
/// inside the same transaction; the FE can use the GET response (or the
/// refresh-worker tick) to populate the badge after a successful PUT.
/// </para>
/// </remarks>
public sealed record SearchableProfileResponse(
    bool IsSearchable,
    string DisplayName,
    string? Headline,
    string? University,
    string? FieldOfStudy,
    int? StudyYear,
    bool ShowHeadline,
    bool ShowUniversity,
    bool ShowFieldOfStudy,
    bool ShowStudyYear,
    DateTimeOffset? OptedInAt,
    DateTimeOffset UpdatedAt,
    int VisibleItemCount);
