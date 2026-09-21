using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="JobPosting"/> (STOR-66). Non-tenant table. RequiredSkills is a text JSON array so browse search can match it with a plain case-insensitive contains.</summary>
public sealed class JobPostingConfiguration : IEntityTypeConfiguration<JobPosting>
{
    public void Configure(EntityTypeBuilder<JobPosting> builder)
    {
        builder.ToTable("JobPostings");

        builder.HasKey(posting => posting.Id);

        builder.Property(posting => posting.OwnerAccountId).IsRequired();

        builder.Property(posting => posting.Title).IsRequired().HasMaxLength(200);
        builder.Property(posting => posting.Kind).IsRequired().HasMaxLength(20);
        builder.Property(posting => posting.CompanyName).IsRequired().HasMaxLength(150);
        builder.Property(posting => posting.Location).HasMaxLength(150);
        builder.Property(posting => posting.WorkMode).IsRequired().HasMaxLength(20);
        builder.Property(posting => posting.Description).IsRequired().HasColumnType("text");

        builder.Property(posting => posting.RequiredSkillsJson)
            .HasColumnName("RequiredSkills")
            .IsRequired()
            .HasColumnType("text");

        builder.Property(posting => posting.Status).IsRequired().HasMaxLength(20);
        builder.Property(posting => posting.CreatedAt).IsRequired();
        builder.Property(posting => posting.UpdatedAt).IsRequired();
        builder.Property(posting => posting.ClosedAt);

        builder.HasOne(posting => posting.Owner)
            .WithMany()
            .HasForeignKey(posting => posting.OwnerAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(posting => posting.OwnerAccountId);
        builder.HasIndex(posting => new { posting.Status, posting.CreatedAt });
    }
}
