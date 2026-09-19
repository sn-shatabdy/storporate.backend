using FluentValidation;
using Storporate.Modules.StudentGrowthExperience.Requests;

namespace Storporate.Modules.StudentGrowthExperience.Validators;

/// <summary>Validates <see cref="AddExplorationMessageRequest"/> before the
/// handler runs. Enforces: at least one of <c>Content</c> or
/// <c>Answers</c> must be present (the request can't be entirely empty);
/// <c>Content</c> is capped at <see cref="MaxContentCharacters"/>
/// characters after trim; each <c>Answer.Question</c> at
/// <see cref="MaxQuestionCharacters"/>; each <c>Answer.Answer</c> at
/// <see cref="MaxAnswerCharacters"/>; at most
/// <see cref="MaxAnswers"/> answers per request.</summary>
public sealed class AddExplorationMessageValidator : AbstractValidator<AddExplorationMessageRequest>
{
    /// <summary>4 000-character cap on the free-text reply.</summary>
    public const int MaxContentCharacters = 4_000;

    /// <summary>1 000-character cap on each question's text inside an answer.</summary>
    public const int MaxQuestionCharacters = 1_000;

    /// <summary>1 000-character cap on each answer's text.</summary>
    public const int MaxAnswerCharacters = 1_000;

    /// <summary>Maximum number of structured answers per request.</summary>
    public const int MaxAnswers = 5;

    public AddExplorationMessageValidator()
    {
        RuleFor(request => request)
            .Must(HasAnyContent)
            .WithErrorCode("message_required")
            .WithMessage("Provide a message or at least one structured answer.");

        RuleFor(request => request.Content ?? string.Empty)
            .MaximumLength(MaxContentCharacters)
            .WithErrorCode("message_too_long")
            .WithMessage($"Message must be at most {MaxContentCharacters} characters.");

        RuleFor(request => request.Answers!.Count)
            .LessThanOrEqualTo(MaxAnswers)
            .WithErrorCode("answers_too_many")
            .WithMessage($"At most {MaxAnswers} answers are allowed per message.")
            .When(request => request.Answers is not null);

        RuleForEach(request => request.Answers!)
            .ChildRules(answer =>
            {
                answer.RuleFor(a => a.Question ?? string.Empty)
                    .NotEmpty()
                    .WithErrorCode("answer_question_required")
                    .WithMessage("Each answer must include the question text.")
                    .MaximumLength(MaxQuestionCharacters)
                    .WithErrorCode("answer_question_too_long")
                    .WithMessage($"Answer question must be at most {MaxQuestionCharacters} characters.");

                answer.RuleFor(a => a.Answer ?? string.Empty)
                    .NotEmpty()
                    .WithErrorCode("answer_required")
                    .WithMessage("Each answer must include a non-empty response.")
                    .MaximumLength(MaxAnswerCharacters)
                    .WithErrorCode("answer_too_long")
                    .WithMessage($"Answer must be at most {MaxAnswerCharacters} characters.");
            })
            .When(request => request.Answers is not null);
    }

    private static bool HasAnyContent(AddExplorationMessageRequest request)
    {
        var hasContent = !string.IsNullOrWhiteSpace(request.Content);
        var hasAnswer = request.Answers is { Count: > 0 };
        return hasContent || hasAnswer;
    }
}
