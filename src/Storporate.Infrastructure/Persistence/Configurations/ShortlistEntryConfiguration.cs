using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="ShortlistEntry"/> (STOR-68). Non-tenant; one entry per (organization, student).</summary>
public sealed class ShortlistEntryConfiguration : IEntityTypeConfiguration<ShortlistEntry>
{
    public void Configure(EntityTypeBuilder<ShortlistEntry> builder)
    {
        builder.ToTable("ShortlistEntries");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.OrganizationAccountId).IsRequired();
        builder.Property(entry => entry.StudentAccountId).IsRequired();
        builder.Property(entry => entry.CandidateId).IsRequired();
        builder.Property(entry => entry.CreatedAt).IsRequired();

        builder.HasOne(entry => entry.Organization)
            .WithMany()
            .HasForeignKey(entry => entry.OrganizationAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(entry => entry.Student)
            .WithMany()
            .HasForeignKey(entry => entry.StudentAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(entry => new { entry.OrganizationAccountId, entry.StudentAccountId }).IsUnique();
        builder.HasIndex(entry => entry.OrganizationAccountId);
    }
}
