namespace Storporate.Modules.StudentGrowthExperience.Advisor;

/// <summary>The two confidence bands the advisor's <c>gaps[].band</c> field
/// is allowed to emit. Anything else is dropped to <c>null</c> by
/// <see cref="AdvisorResponseParser"/>; the plan explicitly forbids numeric
/// scores and arbitrary band labels (Binding design rule: "no numeric scores").
/// </summary>
public static class AdvisorGapBands
{
    public const string Developing = "Developing";
    public const string Missing = "Missing";
}

/// <summary>The typed shape the advisor pipeline stores on the database, after
/// <see cref="AdvisorResponseParser"/> has trimmed, capped, and dropped
/// malformed fields. Distinct from <c>AdvisorJsonDto</c> which mirrors the
/// raw AI contract; the parser maps the DTO onto these records so the rest
/// of the pipeline never has to defend against bad AI output again.</summary>
public sealed record AdvisorParsedResponse(
    string? Reply,
    IReadOnlyList<AdvisorParsedQuestion> Questions,
    string? Title,
    IReadOnlyList<string> ContextNotes,
    AdvisorParsedSummary? Summary);

/// <summary>A single advisor question — a prompt plus a short list of
/// multiple-choice options the student can pick from.</summary>
public sealed record AdvisorParsedQuestion(string Prompt, IReadOnlyList<string> Options);

/// <summary>The summary block of an advisor turn. <see cref="Gaps"/> are
/// skill gaps the model sees in the student's record;
/// <see cref="Suggestions"/> are next-step suggestions, optionally anchored
/// to a <see cref="Storporate.SharedKernel.Entities.FeedItem"/> via
/// <see cref="AdvisorParsedSuggestion.SourceFeedItemId"/>.</summary>
public sealed record AdvisorParsedSummary(
    IReadOnlyList<AdvisorParsedGap> Gaps,
    IReadOnlyList<AdvisorParsedSuggestion> Suggestions,
    string? ChangeNote);

/// <summary>A single skill gap the advisor surfaced. <see cref="Band"/> is one
/// of <see cref="AdvisorGapBands"/> or <c>null</c> when the model emitted
/// something else (parser drops the bad band silently).</summary>
public sealed record AdvisorParsedGap(string Title, string Detail, string? Band);

/// <summary>A single next-step suggestion. <see cref="SourceFeedItemId"/> is
/// non-null only when the AI-emitted id was (a) a valid GUID AND (b) in the
/// candidate id set the prompt supplied; otherwise the field is null and the
/// suggestion keeps <see cref="Title"/> / <see cref="Reason"/> /
/// <see cref="NextStep"/> — the drop-source-keep-rest rule.</summary>
public sealed record AdvisorParsedSuggestion(
    string Title,
    string Reason,
    string NextStep,
    Guid? SourceFeedItemId);
