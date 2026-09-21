using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="OutreachMessage"/> (STOR-68). Non-tenant; deleted with its conversation.</summary>
public sealed class OutreachMessageConfiguration : IEntityTypeConfiguration<OutreachMessage>
{
    public void Configure(EntityTypeBuilder<OutreachMessage> builder)
    {
        builder.ToTable("OutreachMessages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.ConversationId).IsRequired();
        builder.Property(message => message.SenderRole).IsRequired().HasMaxLength(20);
        builder.Property(message => message.Body).IsRequired().HasMaxLength(2000);
        builder.Property(message => message.CreatedAt).IsRequired();

        builder.HasOne(message => message.Conversation)
            .WithMany(conversation => conversation.Messages)
            .HasForeignKey(message => message.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(message => new { message.ConversationId, message.CreatedAt });
    }
}
