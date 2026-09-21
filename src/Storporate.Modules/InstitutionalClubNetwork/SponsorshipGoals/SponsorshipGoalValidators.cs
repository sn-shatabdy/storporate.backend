using FluentValidation;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;

/// <summary>Validates <see cref="SaveSponsorshipGoalRequest"/>. Each rule carries an error code + message.</summary>
public sealed class SaveSponsorshipGoalValidator : AbstractValidator<SaveSponsorshipGoalRequest>
{
    public const int NameMin = 2;
    public const int NameMax = 100;
    public const int CompanyNameMin = 2;
    public const int CompanyNameMax = 150;
    public const int ObjectivesMax = 6;
    public const int EventKindsMax = 8;
    public const int FieldsMax = 15;
    public const int FieldMin = 2;
    public const int FieldMax = 60;
    public const int CitiesMax = 10;
    public const int CityMin = 2;
    public const int CityMax = 60;
    public const int UniversitiesMax = 10;
    public const int UniversityMin = 2;
    public const int UniversityMax = 150;
    public const int YearMin = 1;
    public const int YearMax = 6;
    public const int BudgetMax = 1_000_000_000;
    public const int NotesMax = 1000;

    public SaveSponsorshipGoalValidator()
    {
        RuleFor(r => (r.Name ?? string.Empty).Trim().Length)
            .InclusiveBetween(NameMin, NameMax)
                .WithErrorCode("sponsorship_goal_name_invalid")
                .WithMessage($"Name must be between {NameMin} and {NameMax} characters.");

        RuleFor(r => (r.CompanyName ?? string.Empty).Trim().Length)
            .InclusiveBetween(CompanyNameMin, CompanyNameMax)
                .WithErrorCode("sponsorship_goal_company_name_invalid")
                .WithMessage($"Company name must be between {CompanyNameMin} and {CompanyNameMax} characters.");

        RuleFor(r => r.Objectives)
            .Cascade(CascadeMode.Stop)
            .Must(o => o is not null && o.All(x => SponsorshipGoalOptions.IsInList(x, SponsorshipGoalOptions.Objectives)))
                .WithErrorCode("sponsorship_goal_objectives_invalid")
                .WithMessage($"Objectives must come from: {string.Join(", ", SponsorshipGoalOptions.Objectives)}.")
            .Must(o => SponsorshipGoalOptions.NormalizeFromList(o!, SponsorshipGoalOptions.Objectives).Count is >= 1 and <= ObjectivesMax)
                .WithErrorCode("sponsorship_goal_objectives_invalid")
                .WithMessage($"Choose between 1 and {ObjectivesMax} objectives.");

        RuleFor(r => r.EventKinds)
            .Cascade(CascadeMode.Stop)
            .Must(e => e is not null && e.All(x => SponsorshipGoalOptions.IsInList(x, SponsorshipGoalOptions.EventKinds)))
                .WithErrorCode("sponsorship_goal_event_kinds_invalid")
                .WithMessage($"Event kinds must come from: {string.Join(", ", SponsorshipGoalOptions.EventKinds)}.")
            .Must(e => SponsorshipGoalOptions.NormalizeFromList(e!, SponsorshipGoalOptions.EventKinds).Count is >= 1 and <= EventKindsMax)
                .WithErrorCode("sponsorship_goal_event_kinds_invalid")
                .WithMessage($"Choose between 1 and {EventKindsMax} event kinds.");

        RuleFor(r => r.Audience)
            .Cascade(CascadeMode.Stop)
            .NotNull()
                .WithErrorCode("sponsorship_goal_audience_required")
                .WithMessage("Add at least one audience: a field of study, study year, city or university.")
            .Must(a => a!.FieldsOfStudy is null || a.FieldsOfStudy.All(f => f?.Trim().Length is >= FieldMin and <= FieldMax))
                .WithErrorCode("sponsorship_goal_field_of_study_invalid")
                .WithMessage($"Each field of study must be between {FieldMin} and {FieldMax} characters.")
            .Must(a => a!.FieldsOfStudy is null || SponsorshipGoalOptions.NormalizeNames(a.FieldsOfStudy).Count <= FieldsMax)
                .WithErrorCode("sponsorship_goal_fields_of_study_count_invalid")
                .WithMessage($"Add at most {FieldsMax} different fields of study.")
            .Must(a => a!.Years is null || a.Years.All(y => y is >= YearMin and <= YearMax))
                .WithErrorCode("sponsorship_goal_years_invalid")
                .WithMessage($"Study years must be between {YearMin} and {YearMax}.")
            .Must(a => a!.Cities is null || a.Cities.All(c => c?.Trim().Length is >= CityMin and <= CityMax))
                .WithErrorCode("sponsorship_goal_city_invalid")
                .WithMessage($"Each city must be between {CityMin} and {CityMax} characters.")
            .Must(a => a!.Cities is null || SponsorshipGoalOptions.NormalizeNames(a.Cities).Count <= CitiesMax)
                .WithErrorCode("sponsorship_goal_cities_count_invalid")
                .WithMessage($"Add at most {CitiesMax} different cities.")
            .Must(a => a!.Universities is null || a.Universities.All(u => u?.Trim().Length is >= UniversityMin and <= UniversityMax))
                .WithErrorCode("sponsorship_goal_university_invalid")
                .WithMessage($"Each university must be between {UniversityMin} and {UniversityMax} characters.")
            .Must(a => a!.Universities is null || SponsorshipGoalOptions.NormalizeNames(a.Universities).Count <= UniversitiesMax)
                .WithErrorCode("sponsorship_goal_universities_count_invalid")
                .WithMessage($"Add at most {UniversitiesMax} different universities.")
            .Must(HasAnyAudienceDimension)
                .WithErrorCode("sponsorship_goal_audience_required")
                .WithMessage("Add at least one audience: a field of study, study year, city or university.");

        RuleFor(r => r.Budget)
            .Cascade(CascadeMode.Stop)
            .Must(b => b is null || (IsValidAmount(b.Min) && IsValidAmount(b.Max)))
                .WithErrorCode("sponsorship_goal_budget_invalid")
                .WithMessage($"Budget amounts must be between 0 and {BudgetMax:N0} BDT.")
            .Must(b => b is null || b.Min is null || b.Max is null || b.Min <= b.Max)
                .WithErrorCode("sponsorship_goal_budget_invalid")
                .WithMessage("The minimum budget cannot be higher than the maximum budget.");

        RuleFor(r => (r.Notes ?? string.Empty).Trim().Length)
            .LessThanOrEqualTo(NotesMax)
                .WithErrorCode("sponsorship_goal_notes_invalid")
                .WithMessage($"Notes must be at most {NotesMax} characters.");
    }

    private static bool IsValidAmount(int? amount) => amount is null || amount is >= 0 and <= BudgetMax;

    private static bool HasAnyAudienceDimension(SponsorshipAudienceRequest? a) =>
        a is not null
        && (SponsorshipGoalOptions.NormalizeNames(a.FieldsOfStudy ?? []).Count > 0
            || (a.Years ?? []).Count > 0
            || SponsorshipGoalOptions.NormalizeNames(a.Cities ?? []).Count > 0
            || SponsorshipGoalOptions.NormalizeNames(a.Universities ?? []).Count > 0);
}

/// <summary>Validates <see cref="SetSponsorshipGoalStatusRequest"/>.</summary>
public sealed class SetSponsorshipGoalStatusValidator : AbstractValidator<SetSponsorshipGoalStatusRequest>
{
    public SetSponsorshipGoalStatusValidator()
    {
        RuleFor(r => r.Status)
            .Must(s => s is not null && Storporate.SharedKernel.Entities.SponsorshipGoalStatuses.All.Contains(s))
                .WithErrorCode("sponsorship_goal_status_invalid")
                .WithMessage("Status must be Active or Paused.");
    }
}
