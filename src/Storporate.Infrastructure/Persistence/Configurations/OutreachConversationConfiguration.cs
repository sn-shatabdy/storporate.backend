using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="OutreachConversation"/> (STOR-68). Non-tenant; one conversation per (organization, student).</summary>
public sealed class OutreachConversationConfiguration : IEntityTypeConfiguration<OutreachConversation>
{
    public void Configure(EntityTypeBuilder<OutreachConversation> builder)
    {
        builder.ToTable("OutreachConversations");

        builder.HasKey(conversation => conversation.Id);

        builder.Property(conversation => conversation.OrganizationAccountId).IsRequired();
        builder.Property(conversation => conversation.StudentAccountId).IsRequired();
        builder.Property(conversation => conversation.OrganizationName).IsRequired().HasMaxLength(150);
        builder.Property(conversation => conversation.StudentDisplayName).IsRequired().HasMaxLength(150);
        builder.Property(conversation => conversation.Status).IsRequired().HasMaxLength(20);
        builder.Property(conversation => conversation.CreatedAt).IsRequired();
        builder.Property(conversation => conversation.UpdatedAt).IsRequired();

        builder.HasOne(conversation => conversation.Organization)
            .WithMany()
            .HasForeignKey(conversation => conversation.OrganizationAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(conversation => conversation.Student)
            .WithMany()
            .HasForeignKey(conversation => conversation.StudentAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(conversation => new { conversation.OrganizationAccountId, conversation.StudentAccountId }).IsUnique();
        builder.HasIndex(conversation => conversation.OrganizationAccountId);
        builder.HasIndex(conversation => conversation.StudentAccountId);
    }
}
