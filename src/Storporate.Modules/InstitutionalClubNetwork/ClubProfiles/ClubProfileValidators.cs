using FluentValidation;

namespace Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;

/// <summary>Validates <see cref="SaveClubProfileRequest"/>. Each rule carries an error code + message.</summary>
public sealed class SaveClubProfileValidator : AbstractValidator<SaveClubProfileRequest>
{
    public const int NameMin = 2;
    public const int NameMax = 150;
    public const int TaglineMax = 200;
    public const int AboutMin = 20;
    public const int AboutMax = 3000;
    public const int UniversityMin = 2;
    public const int UniversityMax = 150;
    public const int CityMax = 100;
    public const int FoundedYearMin = 1900;
    public const int MemberCountMax = 1_000_000;
    public const int FieldsMax = 15;
    public const int FieldMin = 2;
    public const int FieldMax = 60;
    public const int YearMin = 1;
    public const int YearMax = 6;
    public const int EventsMax = 12;
    public const int EventTitleMin = 3;
    public const int EventTitleMax = 120;
    public const int EventDescriptionMax = 1000;
    public const int AttendanceMin = 1;
    public const int AttendanceMax = 100_000;
    public const int SupportNeedsMax = 8;

    public SaveClubProfileValidator(TimeProvider timeProvider)
    {
        RuleFor(r => (r.Name ?? string.Empty).Trim().Length)
            .InclusiveBetween(NameMin, NameMax)
                .WithErrorCode("club_profile_name_invalid")
                .WithMessage($"Club name must be between {NameMin} and {NameMax} characters.");

        RuleFor(r => (r.Tagline ?? string.Empty).Trim().Length)
            .LessThanOrEqualTo(TaglineMax)
                .WithErrorCode("club_profile_tagline_invalid")
                .WithMessage($"Tagline must be at most {TaglineMax} characters.");

        RuleFor(r => (r.About ?? string.Empty).Trim().Length)
            .InclusiveBetween(AboutMin, AboutMax)
                .WithErrorCode("club_profile_about_invalid")
                .WithMessage($"About must be between {AboutMin} and {AboutMax} characters.");

        RuleFor(r => (r.University ?? string.Empty).Trim().Length)
            .InclusiveBetween(UniversityMin, UniversityMax)
                .WithErrorCode("club_profile_university_invalid")
                .WithMessage($"University must be between {UniversityMin} and {UniversityMax} characters.");

        RuleFor(r => (r.City ?? string.Empty).Trim().Length)
            .LessThanOrEqualTo(CityMax)
                .WithErrorCode("club_profile_city_invalid")
                .WithMessage($"City must be at most {CityMax} characters.");

        RuleFor(r => r.FoundedYear)
            .Must(y => y is null || (y >= FoundedYearMin && y <= timeProvider.GetUtcNow().Year))
                .WithErrorCode("club_profile_founded_year_invalid")
                .WithMessage($"Founded year must be between {FoundedYearMin} and the current year.");

        RuleFor(r => r.MemberCount)
            .Must(c => c is >= 0 and <= MemberCountMax)
                .WithErrorCode("club_profile_member_count_invalid")
                .WithMessage($"Member count must be a number between 0 and {MemberCountMax:N0}.");

        RuleFor(r => r.Audience)
            .Cascade(CascadeMode.Stop)
            .Must(a => a?.FieldsOfStudy is null || a.FieldsOfStudy.All(f => f?.Trim().Length is >= FieldMin and <= FieldMax))
                .WithErrorCode("club_profile_field_of_study_invalid")
                .WithMessage($"Each field of study must be between {FieldMin} and {FieldMax} characters.")
            .Must(a => a?.FieldsOfStudy is null || ClubProfileOptions.NormalizeNames(a.FieldsOfStudy).Count <= FieldsMax)
                .WithErrorCode("club_profile_fields_of_study_count_invalid")
                .WithMessage($"Add at most {FieldsMax} different fields of study.")
            .Must(a => a?.Years is null || a.Years.All(y => y is >= YearMin and <= YearMax))
                .WithErrorCode("club_profile_years_invalid")
                .WithMessage($"Study years must be between {YearMin} and {YearMax}.");

        RuleFor(r => r.Events)
            .Must(e => e is null || e.Count <= EventsMax)
                .WithErrorCode("club_profile_events_count_invalid")
                .WithMessage($"Add at most {EventsMax} events.")
            .Must(e => e is null || e.All(x => x is not null))
                .WithErrorCode("club_profile_event_invalid")
                .WithMessage("Each event must be filled in.");

        RuleForEach(r => r.Events!)
            .Where(e => e is not null)
            .SetValidator(new ClubEventValidator())
            .When(r => r.Events is not null);
    }
}

public sealed class ClubEventValidator : AbstractValidator<ClubEventRequest>
{
    public ClubEventValidator()
    {
        RuleFor(e => (e.Title ?? string.Empty).Trim().Length)
            .InclusiveBetween(SaveClubProfileValidator.EventTitleMin, SaveClubProfileValidator.EventTitleMax)
                .WithErrorCode("club_profile_event_title_invalid")
                .WithMessage($"Event title must be between {SaveClubProfileValidator.EventTitleMin} and {SaveClubProfileValidator.EventTitleMax} characters.");

        RuleFor(e => (e.Description ?? string.Empty).Trim().Length)
            .LessThanOrEqualTo(SaveClubProfileValidator.EventDescriptionMax)
                .WithErrorCode("club_profile_event_description_invalid")
                .WithMessage($"Event description must be at most {SaveClubProfileValidator.EventDescriptionMax} characters.");

        RuleFor(e => e.TypicalAttendance)
            .Must(a => a is >= SaveClubProfileValidator.AttendanceMin and <= SaveClubProfileValidator.AttendanceMax)
                .WithErrorCode("club_profile_event_attendance_invalid")
                .WithMessage($"Typical attendance must be between {SaveClubProfileValidator.AttendanceMin} and {SaveClubProfileValidator.AttendanceMax:N0}.");

        RuleFor(e => e.Frequency)
            .Must(f => f is not null && ClubProfileOptions.Frequencies.Contains(f))
                .WithErrorCode("club_profile_event_frequency_invalid")
                .WithMessage("Event frequency must be OneOff, Monthly, Termly or Yearly.");

        RuleFor(e => e.SupportNeeds)
            .Must(s => s is null || s.All(n => n is not null && ClubProfileOptions.SupportNeeds.Contains(n)))
                .WithErrorCode("club_profile_event_support_need_invalid")
                .WithMessage($"Support needs must come from: {string.Join(", ", ClubProfileOptions.SupportNeeds)}.")
            .Must(s => s is null || s.Distinct(StringComparer.Ordinal).Count() <= SaveClubProfileValidator.SupportNeedsMax)
                .WithErrorCode("club_profile_event_support_need_invalid")
                .WithMessage($"Add at most {SaveClubProfileValidator.SupportNeedsMax} support needs per event.");
    }
}
