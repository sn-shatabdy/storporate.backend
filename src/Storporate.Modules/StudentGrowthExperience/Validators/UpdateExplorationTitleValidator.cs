using FluentValidation;
using Storporate.Modules.StudentGrowthExperience.Requests;

namespace Storporate.Modules.StudentGrowthExperience.Validators;

/// <summary>Validates <see cref="UpdateExplorationTitleRequest"/> before the
/// handler runs. The title is required (1-200 characters after trim).</summary>
public sealed class UpdateExplorationTitleValidator : AbstractValidator<UpdateExplorationTitleRequest>
{
    /// <summary>200-character cap on the exploration title.</summary>
    public const int MaxTitleCharacters = 200;

    public UpdateExplorationTitleValidator()
    {
        RuleFor(request => request.Title ?? string.Empty)
            .NotEmpty()
            .WithErrorCode("title_required")
            .WithMessage("Title must not be empty.")
            .MaximumLength(MaxTitleCharacters)
            .WithErrorCode("title_too_long")
            .WithMessage($"Title must be at most {MaxTitleCharacters} characters.");
    }
}
