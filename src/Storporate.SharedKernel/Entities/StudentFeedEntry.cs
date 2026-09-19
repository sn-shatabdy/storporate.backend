namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The fact that a particular <see cref="FeedItem"/> is relevant to a
/// particular student. Created by the Phase 3 <c>MatchFeedJobProcessor</c>
/// per-account, deleted by the student's hard-delete pipeline. Always read
/// alongside the corresponding <see cref="FeedItem"/>; the entity exists so
/// the relevance reason (which the AI generated, specific to the student)
/// can be stored next to the join.
/// </summary>
public sealed class StudentFeedEntry : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public User? Account { get; set; }

    /// <summary>The feed item being matched to <see cref="AccountId"/>.</summary>
    public Guid FeedItemId { get; set; }

    public FeedItem? FeedItem { get; set; }

    /// <summary>The AI-generated reason this item is relevant to the student.
    /// Surfaced in the advisor feed page alongside the item's title and
    /// source link.</summary>
    public required string Reason { get; set; }

    public DateTimeOffset MatchedAt { get; set; }
}