namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A single message in an <see cref="Exploration"/>. Every message is either
/// a <see cref="ExplorationRoles.Student"/> (user-authored) or
/// <see cref="ExplorationRoles.Advisor"/> (AI-authored) turn. Advisor turns
/// optionally carry structured <see cref="QuestionsJson"/> for the per-turn
/// "questions card" UI element; plain-reply advisor turns leave it null.
/// </summary>
public sealed class ExplorationMessage : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public User? Account { get; set; }

    public Guid ExplorationId { get; set; }

    public Exploration? Exploration { get; set; }

    /// <summary>One of <see cref="ExplorationRoles"/>.</summary>
    public required string Role { get; set; }

    /// <summary>Plain-text message body. The advisor pipeline renders this
    /// as paragraph-broken plain text — no HTML.</summary>
    public required string Content { get; set; }

    /// <summary>JSON-encoded array of question objects the advisor emitted
    /// alongside this reply. The Phase 2 advisor prompt contract allows
    /// <c>questions</c> as a sibling of <c>reply</c>; the per-turn UI reads
    /// this column to render option chips. Null on Student turns and on
    /// plain-reply advisor turns.</summary>
    public string? QuestionsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}