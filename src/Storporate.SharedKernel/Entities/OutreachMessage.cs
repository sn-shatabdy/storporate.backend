namespace Storporate.SharedKernel.Entities;

/// <summary>A single message inside an <see cref="OutreachConversation"/> (STOR-68).</summary>
public sealed class OutreachMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public OutreachConversation? Conversation { get; set; }

    /// <summary>One of <see cref="OutreachSenderRoles"/>.</summary>
    public required string SenderRole { get; set; }

    public required string Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
