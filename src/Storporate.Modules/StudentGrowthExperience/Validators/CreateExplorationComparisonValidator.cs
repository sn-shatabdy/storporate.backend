using FluentValidation;
using Storporate.Modules.StudentGrowthExperience.Requests;

namespace Storporate.Modules.StudentGrowthExperience.Validators;

/// <summary>Validates <see cref="CreateExplorationComparisonRequest"/> before
/// the handler runs. The two ids must differ — comparing an exploration
/// against itself is rejected with <c>400 compare_needs_two</c>.</summary>
public sealed class CreateExplorationComparisonValidator : AbstractValidator<CreateExplorationComparisonRequest>
{
    public CreateExplorationComparisonValidator()
    {
        RuleFor(request => request.FirstExplorationId)
            .NotEqual(request => request.SecondExplorationId)
            .WithErrorCode("compare_needs_two")
            .WithMessage("First and second exploration ids must differ.");
    }
}
