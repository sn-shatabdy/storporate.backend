using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="ExplorationMessage"/>. STOR-40 Phase 1:
/// tenant table; implements <see cref="IAccountScoped"/> and inherits the
/// global query filter + save-time interceptor + Postgres RLS policy the
/// other Phase 1 student-growth tables share.
/// </summary>
public sealed class ExplorationMessageConfiguration : IEntityTypeConfiguration<ExplorationMessage>
{
    public void Configure(EntityTypeBuilder<ExplorationMessage> builder)
    {
        builder.ToTable("ExplorationMessages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.AccountId)
            .IsRequired();

        builder.Property(message => message.ExplorationId)
            .IsRequired();

        builder.Property(message => message.Role)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(message => message.Content)
            .IsRequired()
            .HasColumnType("text");

        // jsonb: the per-turn "questions card" UI reads it as a JSON array,
        // and jsonb lets future story work query inside it without a schema
        // change.
        builder.Property(message => message.QuestionsJson)
            .HasColumnType("jsonb");

        builder.Property(message => message.CreatedAt)
            .IsRequired();

        builder.HasIndex(message => message.AccountId);
        builder.HasIndex(message => new { message.ExplorationId, message.CreatedAt });

        builder.HasOne(message => message.Account)
            .WithMany()
            .HasForeignKey(message => message.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(message => message.Exploration)
            .WithMany()
            .HasForeignKey(message => message.ExplorationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}