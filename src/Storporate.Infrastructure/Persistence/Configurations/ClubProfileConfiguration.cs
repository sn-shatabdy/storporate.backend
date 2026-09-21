using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="ClubProfile"/> (STOR-69). Non-tenant table: Organizations read Published profiles of other accounts, so scoping is explicit in the handlers. One profile per club account (unique owner).</summary>
public sealed class ClubProfileConfiguration : IEntityTypeConfiguration<ClubProfile>
{
    public void Configure(EntityTypeBuilder<ClubProfile> builder)
    {
        builder.ToTable("ClubProfiles");

        builder.HasKey(profile => profile.Id);

        builder.Property(profile => profile.OwnerAccountId).IsRequired();

        builder.Property(profile => profile.Name).IsRequired().HasMaxLength(150);
        builder.Property(profile => profile.Tagline).HasMaxLength(200);
        builder.Property(profile => profile.About).IsRequired().HasColumnType("text");
        builder.Property(profile => profile.University).IsRequired().HasMaxLength(150);
        builder.Property(profile => profile.City).HasMaxLength(100);
        builder.Property(profile => profile.FoundedYear);
        builder.Property(profile => profile.MemberCount).IsRequired();

        builder.Property(profile => profile.AudienceFieldsOfStudy).IsRequired().HasColumnType("text");
        builder.Property(profile => profile.AudienceYears).IsRequired().HasColumnType("text");
        builder.Property(profile => profile.EventsJson).HasColumnName("Events").IsRequired().HasColumnType("text");

        builder.Property(profile => profile.Status).IsRequired().HasMaxLength(20);
        builder.Property(profile => profile.CreatedAt).IsRequired();
        builder.Property(profile => profile.UpdatedAt).IsRequired();
        builder.Property(profile => profile.PublishedAt);

        builder.HasOne(profile => profile.Owner)
            .WithMany()
            .HasForeignKey(profile => profile.OwnerAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(profile => profile.OwnerAccountId).IsUnique();
        builder.HasIndex(profile => new { profile.Status, profile.University });
    }
}
