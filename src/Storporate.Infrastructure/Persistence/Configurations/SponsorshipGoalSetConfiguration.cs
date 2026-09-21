using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="SponsorshipGoalSet"/> (STOR-70). Non-tenant table: clubs read Active sets of other accounts, so scoping is explicit in the handlers. A company may own several sets (owner is indexed, not unique).</summary>
public sealed class SponsorshipGoalSetConfiguration : IEntityTypeConfiguration<SponsorshipGoalSet>
{
    public void Configure(EntityTypeBuilder<SponsorshipGoalSet> builder)
    {
        builder.ToTable("SponsorshipGoalSets");

        builder.HasKey(set => set.Id);

        builder.Property(set => set.OwnerAccountId).IsRequired();

        builder.Property(set => set.Name).IsRequired().HasMaxLength(100);
        builder.Property(set => set.CompanyName).IsRequired().HasMaxLength(150);

        builder.Property(set => set.Objectives).IsRequired().HasColumnType("text");
        builder.Property(set => set.AudienceFieldsOfStudy).IsRequired().HasColumnType("text");
        builder.Property(set => set.AudienceYears).IsRequired().HasColumnType("text");
        builder.Property(set => set.AudienceCities).IsRequired().HasColumnType("text");
        builder.Property(set => set.AudienceUniversities).IsRequired().HasColumnType("text");
        builder.Property(set => set.EventKinds).IsRequired().HasColumnType("text");

        builder.Property(set => set.BudgetMin);
        builder.Property(set => set.BudgetMax);
        builder.Property(set => set.ShowBudget).IsRequired().HasDefaultValue(false);
        builder.Property(set => set.Notes).HasColumnType("text");

        builder.Property(set => set.Status).IsRequired().HasMaxLength(20);
        builder.Property(set => set.CreatedAt).IsRequired();
        builder.Property(set => set.UpdatedAt).IsRequired();

        builder.HasOne(set => set.Owner)
            .WithMany()
            .HasForeignKey(set => set.OwnerAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(set => set.OwnerAccountId);
        builder.HasIndex(set => new { set.Status, set.CreatedAt });
    }
}
