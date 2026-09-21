using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="SponsorshipRequestMessage"/> (STOR-72). Non-tenant; deleted with its request.</summary>
public sealed class SponsorshipRequestMessageConfiguration : IEntityTypeConfiguration<SponsorshipRequestMessage>
{
    public void Configure(EntityTypeBuilder<SponsorshipRequestMessage> builder)
    {
        builder.ToTable("SponsorshipRequestMessages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.RequestId).IsRequired();
        builder.Property(message => message.SenderSide).IsRequired().HasMaxLength(20);
        builder.Property(message => message.Body).IsRequired().HasColumnType("text");
        builder.Property(message => message.CreatedAt).IsRequired();

        builder.HasOne(message => message.Request)
            .WithMany(request => request.Messages)
            .HasForeignKey(message => message.RequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(message => new { message.RequestId, message.CreatedAt });
    }
}
