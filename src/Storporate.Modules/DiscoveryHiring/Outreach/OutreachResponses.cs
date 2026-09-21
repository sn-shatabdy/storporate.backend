namespace Storporate.Modules.DiscoveryHiring.Outreach;

/// <summary>Conversation reference shown on a shortlist row. Never carries an account id.</summary>
public sealed record ShortlistConversationResponse(Guid Id, string Status);

/// <summary>
/// One shortlisted candidate. Display fields come from the current talent index entry and are all
/// null (with <c>Available = false</c>) once the student has opted out.
/// </summary>
public sealed record ShortlistEntryResponse(
    Guid CandidateId,
    string? DisplayName,
    string? Headline,
    string? University,
    string? FieldOfStudy,
    int? StudyYear,
    bool Available,
    DateTimeOffset SavedAt,
    ShortlistConversationResponse? Conversation);

public sealed record ShortlistListResponse(IReadOnlyList<ShortlistEntryResponse> Items);

public sealed record ConversationSummaryResponse(
    Guid Id,
    string CounterpartName,
    string Status,
    string LastMessagePreview,
    DateTimeOffset LastMessageAt,
    DateTimeOffset UpdatedAt);

public sealed record ConversationListResponse(IReadOnlyList<ConversationSummaryResponse> Items);

public sealed record OutreachMessageResponse(
    Guid Id,
    string SenderRole,
    string Body,
    DateTimeOffset CreatedAt,
    bool FromMe);

public sealed record ConversationDetailResponse(
    Guid Id,
    string CounterpartName,
    string Status,
    string LastMessagePreview,
    DateTimeOffset LastMessageAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OutreachMessageResponse> Messages);
