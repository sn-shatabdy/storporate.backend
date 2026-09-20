using FluentValidation;
using Storporate.Modules.DiscoveryHiring.SearchableProfile;

namespace Storporate.Modules.DiscoveryHiring.SearchableProfile;

/// <summary>
/// Validates the <see cref="UpdateSearchableProfileRequest"/> shape before
/// the handler runs. Enforces the four "real" invariants the plan calls
/// out: a non-blank display name when the student is opting in, the per-
/// field length caps (80 / 120 / 120 / 120 / year 1-8), and a no-op rule
/// that lets the student toggle <see cref="UpdateSearchableProfileRequest.IsSearchable"/>
/// to false without supplying a display name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Error codes.</b> Each rule carries an explicit
/// <c>.WithErrorCode(...)</c> + <c>.WithMessage(...)</c> pair. The codes
/// (<c>display_name_required</c>, <c>display_name_too_long</c>,
/// <c>headline_too_long</c>, <c>university_too_long</c>,
/// <c>field_of_study_too_long</c>, <c>study_year_out_of_range</c>) are
/// the wire-level contract the FE renders against — see the Phase 3 page's
/// empty-state copy.
/// </para>
/// <para>
/// <b>Why "blank display name when opting in" is the only opt-in gate.</b>
/// The plan's confirmed-decisions list calls out that an opted-in student
/// with no name is a non-sensical state (employers would see an empty
/// card). The validator returns the same <c>display_name_required</c> error
/// code regardless of whether the student supplied <c>null</c>,
/// <c>""</c>, or whitespace — the FE's validation message is identical.
/// </para>
/// </remarks>
public sealed class UpdateSearchableProfileValidator : AbstractValidator<UpdateSearchableProfileRequest>
{
    /// <summary>Upper bound on <see cref="UpdateSearchableProfileRequest.DisplayName"/>.
    /// Matches the <c>StudentSearchProfileConfiguration.DisplayNameMaxLength</c>
    /// constant (80) so a passing payload always fits the column.</summary>
    public const int DisplayNameMaxLength = 80;

    /// <summary>Upper bound on the self-reported headline.</summary>
    public const int HeadlineMaxLength = 120;

    /// <summary>Upper bound on the self-reported university.</summary>
    public const int UniversityMaxLength = 120;

    /// <summary>Upper bound on the self-reported field of study.</summary>
    public const int FieldOfStudyMaxLength = 120;

    /// <summary>The lowest allowed <see cref="UpdateSearchableProfileRequest.StudyYear"/>.</summary>
    public const int StudyYearMin = 1;

    /// <summary>The highest allowed <see cref="UpdateSearchableProfileRequest.StudyYear"/>.</summary>
    public const int StudyYearMax = 8;

    public UpdateSearchableProfileValidator()
    {
        RuleFor(request => request.DisplayName)
            .NotEmpty()
                .When(request => request.IsSearchable == true,
                    ApplyConditionTo.CurrentValidator)
                .WithErrorCode("display_name_required")
                .WithMessage("Display name is required when the profile is searchable.")
            .MaximumLength(DisplayNameMaxLength)
                .WithErrorCode("display_name_too_long")
                .WithMessage($"Display name must be {DisplayNameMaxLength} characters or fewer.");

        RuleFor(request => request.Headline)
            .MaximumLength(HeadlineMaxLength)
                .WithErrorCode("headline_too_long")
                .WithMessage($"Headline must be {HeadlineMaxLength} characters or fewer.");

        RuleFor(request => request.University)
            .MaximumLength(UniversityMaxLength)
                .WithErrorCode("university_too_long")
                .WithMessage($"University must be {UniversityMaxLength} characters or fewer.");

        RuleFor(request => request.FieldOfStudy)
            .MaximumLength(FieldOfStudyMaxLength)
                .WithErrorCode("field_of_study_too_long")
                .WithMessage($"Field of study must be {FieldOfStudyMaxLength} characters or fewer.");

        RuleFor(request => request.StudyYear!.Value)
            .InclusiveBetween(StudyYearMin, StudyYearMax)
                .When(request => request.StudyYear.HasValue,
                    ApplyConditionTo.CurrentValidator)
                .WithErrorCode("study_year_out_of_range")
                .WithMessage($"Study year must be between {StudyYearMin} and {StudyYearMax}.");
    }
}
