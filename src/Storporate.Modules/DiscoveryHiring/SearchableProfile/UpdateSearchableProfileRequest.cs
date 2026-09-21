namespace Storporate.Modules.DiscoveryHiring.SearchableProfile;

/// <summary>
/// The JSON body for <c>PUT /api/discovery/searchable-profile</c>. Each
/// field is optional so a student can edit the show flags without
/// resending the entire profile; the validator enforces
/// "blank <see cref="DisplayName"/> when <see cref="IsSearchable"/> is true"
/// and the per-field length caps.
/// </summary>
/// <remarks>
/// <para>
/// All fields are nullable on purpose: the validator treats
/// <c>null</c> vs <c>""</c> vs <c>"  "</c> as the same "no value supplied"
/// shape so a partial PUT (only toggles, only display name) round-trips
/// correctly. The handler then merges with the existing row (or applies
/// safe defaults when no row exists yet) so a PUT with <c>{isSearchable: true}</c>
/// alone doesn't accidentally blank out the previously-saved display name.
/// </para>
/// </remarks>
public sealed class UpdateSearchableProfileRequest
{
    /// <summary>True iff the student wants to appear in employer search results.</summary>
    public bool? IsSearchable { get; init; }

    /// <summary>Display name shown to employers. Required when
    /// <see cref="IsSearchable"/> is true; ignored otherwise.</summary>
    public string? DisplayName { get; init; }

    /// <summary>One-line self-description. Optional; capped at 120 chars by the validator.</summary>
    public string? Headline { get; init; }

    /// <summary>Self-reported university. Optional; capped at 120 chars by the validator.</summary>
    public string? University { get; init; }

    /// <summary>Self-reported field of study. Optional; capped at 120 chars by the validator.</summary>
    public string? FieldOfStudy { get; init; }

    /// <summary>Current year of study, 1-8. Optional; validated by the validator when supplied.</summary>
    public int? StudyYear { get; init; }

    /// <summary>True iff the student wants employers to see <see cref="Headline"/>.</summary>
    public bool? ShowHeadline { get; init; }

    /// <summary>True iff the student wants employers to see <see cref="University"/>.</summary>
    public bool? ShowUniversity { get; init; }

    /// <summary>True iff the student wants employers to see <see cref="FieldOfStudy"/>.</summary>
    public bool? ShowFieldOfStudy { get; init; }

    /// <summary>True iff the student wants employers to see <see cref="StudyYear"/>.</summary>
    public bool? ShowStudyYear { get; init; }
}
