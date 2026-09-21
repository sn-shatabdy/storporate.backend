using FluentValidation;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.JobPostings;

/// <summary>Validates <see cref="SaveJobPostingRequest"/>. Each rule carries an error code + message.</summary>
public sealed class SaveJobPostingValidator : AbstractValidator<SaveJobPostingRequest>
{
    public const int TitleMin = 3;
    public const int TitleMax = 120;
    public const int CompanyMin = 2;
    public const int CompanyMax = 150;
    public const int LocationMax = 150;
    public const int DescriptionMin = 20;
    public const int DescriptionMax = 4000;
    public const int SkillsMax = 12;
    public const int SkillMin = 2;
    public const int SkillMax = 40;

    public SaveJobPostingValidator()
    {
        RuleFor(r => (r.Title ?? string.Empty).Trim().Length)
            .InclusiveBetween(TitleMin, TitleMax)
                .WithErrorCode("job_posting_title_invalid")
                .WithMessage($"Title must be between {TitleMin} and {TitleMax} characters.");

        RuleFor(r => r.Kind)
            .Must(k => k is not null && JobPostingKinds.All.Contains(k))
                .WithErrorCode("job_posting_kind_invalid")
                .WithMessage("Kind must be Job or Internship.");

        RuleFor(r => (r.CompanyName ?? string.Empty).Trim().Length)
            .InclusiveBetween(CompanyMin, CompanyMax)
                .WithErrorCode("job_posting_company_invalid")
                .WithMessage($"Company name must be between {CompanyMin} and {CompanyMax} characters.");

        RuleFor(r => (r.Location ?? string.Empty).Trim().Length)
            .LessThanOrEqualTo(LocationMax)
                .WithErrorCode("job_posting_location_invalid")
                .WithMessage($"Location must be at most {LocationMax} characters.");

        RuleFor(r => r.WorkMode)
            .Must(m => m is not null && JobPostingWorkModes.All.Contains(m))
                .WithErrorCode("job_posting_work_mode_invalid")
                .WithMessage("Work mode must be OnSite, Remote or Hybrid.");

        RuleFor(r => (r.Description ?? string.Empty).Trim().Length)
            .InclusiveBetween(DescriptionMin, DescriptionMax)
                .WithErrorCode("job_posting_description_invalid")
                .WithMessage($"Description must be between {DescriptionMin} and {DescriptionMax} characters.");

        RuleFor(r => r.RequiredSkills)
            .Must(skills => skills is not null && skills.All(s => s is not null))
                .WithErrorCode("job_posting_skills_invalid")
                .WithMessage("Required skills must be a list of names.")
            .Must(skills => JobPostingSkills.Normalize(skills!).Count is >= 1 and <= SkillsMax)
                .WithErrorCode("job_posting_skills_count_invalid")
                .WithMessage($"Add between 1 and {SkillsMax} different required skills.")
            .Must(skills => skills!.All(s => s.Trim().Length is >= SkillMin and <= SkillMax))
                .WithErrorCode("job_posting_skill_length_invalid")
                .WithMessage($"Each skill must be between {SkillMin} and {SkillMax} characters.");
    }
}

public sealed class ChangeJobPostingStatusValidator : AbstractValidator<ChangeJobPostingStatusRequest>
{
    public ChangeJobPostingStatusValidator()
    {
        RuleFor(r => r.Status)
            .Must(s => s is not null && JobPostingStatuses.All.Contains(s))
                .WithErrorCode("job_posting_status_invalid")
                .WithMessage("Status must be Open, Paused or Closed.");
    }
}
