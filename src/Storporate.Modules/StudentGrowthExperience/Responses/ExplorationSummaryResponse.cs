namespace Storporate.Modules.StudentGrowthExperience.Responses;

/// <summary>A summary-version snapshot returned on
/// <see cref="ExplorationDetailResponse.LatestSummary"/>.</summary>
public sealed record ExplorationSummaryResponse(
    int VersionNumber,
    DateTimeOffset CreatedAt,
    string? ChangeNote,
    IReadOnlyList<ExplorationGapResponse> Gaps,
    IReadOnlyList<ExplorationSuggestionResponse> Suggestions);

/// <summary>A single gap. <see cref="Band"/> is one of <c>Developing</c> /
/// <c>Missing</c> or null when the AI omitted the band.</summary>
public sealed record ExplorationGapResponse(
    string Title,
    string Detail,
    string? Band);

/// <summary>A single suggestion. <see cref="Source"/> is non-null only when
/// the suggestion was grounded in a candidate the prompt supplied — the
/// server populates the snapshot at write time from the live
/// <see cref="Storporate.SharedKernel.Entities.FeedItem"/> row.</summary>
public sealed record ExplorationSuggestionResponse(
    string Title,
    string Reason,
    string NextStep,
    ExplorationSuggestionSourceResponse? Source);

/// <summary>The source-as-snapshot block on a suggestion. Carries the
/// cited feed item's title / url / sourceName verbatim from the database —
/// never from the AI's text.</summary>
public sealed record ExplorationSuggestionSourceResponse(
    Guid FeedItemId,
    string Title,
    string Url,
    string SourceName);
