namespace Storporate.Modules.StudentGrowthExperience.Requests;

/// <summary>Request body for <c>POST /api/growth/explorations/{id}/messages</c>.
/// The student's free-text reply is concatenated with any structured
/// <see cref="Answers"/> pairs before being persisted as the
/// <see cref="Storporate.SharedKernel.Entities.ExplorationMessage.Content"/>
/// column — the LLM sees a single block of plain text, the UI gets to
/// show the structured pairs separately.</summary>
public sealed class AddExplorationMessageRequest
{
    /// <summary>The student's free-text reply. Capped at 4 000 characters
    /// by <see cref="Validators.AddExplorationMessageValidator"/>. Required
    /// when no <see cref="Answers"/> are provided.</summary>
    public string? Content { get; set; }

    /// <summary>Structured answers to the advisor's last questions. Each
    /// answer's <c>Question</c> is capped at 1 000 characters and
    /// <c>Answer</c> at 1 000 characters; at most 5 pairs are accepted.
    /// Optional — a free-text reply without structured answers is valid.</summary>
    public IReadOnlyList<AddExplorationMessageAnswer>? Answers { get; set; }
}

/// <summary>A single answer pair — the question the advisor asked and the
/// student's structured response.</summary>
public sealed class AddExplorationMessageAnswer
{
    public string? Question { get; set; }
    public string? Answer { get; set; }
}
