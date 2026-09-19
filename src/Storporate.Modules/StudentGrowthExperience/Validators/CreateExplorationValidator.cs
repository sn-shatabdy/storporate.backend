using FluentValidation;
using Storporate.Modules.StudentGrowthExperience.Requests;

namespace Storporate.Modules.StudentGrowthExperience.Validators;

/// <summary>Validates <see cref="CreateExplorationRequest"/> before the handler
/// runs. The optional <c>Direction</c> field is capped at
/// <see cref="MaxDirectionCharacters"/> characters after trim; an
/// all-whitespace direction is treated as absent.</summary>
public sealed class CreateExplorationValidator : AbstractValidator<CreateExplorationRequest>
{
    /// <summary>4 000-character cap on the student's opening direction.
    /// Matches the message-content cap so the same validator chain enforces
    /// both the create path and the add-message path.</summary>
    public const int MaxDirectionCharacters = 4_000;

    public CreateExplorationValidator()
    {
        RuleFor(request => request.Direction ?? string.Empty)
            .MaximumLength(MaxDirectionCharacters)
            .WithErrorCode("direction_too_long")
            .WithMessage($"Direction must be at most {MaxDirectionCharacters} characters.");
    }
}
