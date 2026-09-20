namespace Storporate.Modules.StudentGrowthExperience.Responses;

/// <summary>A single message in an <see cref="ExplorationDetailResponse"/>.
/// The <see cref="Questions"/> array is populated only when the message is
/// an Advisor turn that carried questions alongside its reply.</summary>
public sealed record ExplorationMessageResponse(
    Guid Id,
    string Role,
    string Content,
    IReadOnlyList<ExplorationQuestionResponse>? Questions,
    DateTimeOffset CreatedAt);

/// <summary>A single advisor question — the prompt and the option chips the
/// student can pick from.</summary>
public sealed record ExplorationQuestionResponse(
    string Prompt,
    IReadOnlyList<string> Options);
