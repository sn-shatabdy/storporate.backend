using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="PortfolioItem"/>. Follows the standalone-configuration
/// style established by <see cref="JobConfiguration"/> (and the other
/// STOR-61 configurations in this folder) — table naming + index on
/// <c>AccountId</c> + <see cref="DeleteBehavior.Restrict"/> foreign key to
/// <c>Users.Id</c>, so the global query filter installed by
/// <see cref="WriteDbContext.OnModelCreating"/> resolves against the same index
/// <see cref="JobConfiguration"/> uses, and STOR-62's three-layer isolation
/// pipeline applies to portfolio rows with zero new isolation code.
/// </summary>
public sealed class PortfolioItemConfiguration : IEntityTypeConfiguration<PortfolioItem>
{
    public void Configure(EntityTypeBuilder<PortfolioItem> builder)
    {
        builder.ToTable("PortfolioItems");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.AccountId)
            .IsRequired();

        builder.Property(item => item.Label)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(item => item.Category)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(item => item.CustomCategoryText)
            .HasMaxLength(200);

        builder.Property(item => item.SubmissionType)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(item => item.StorageKey)
            .HasMaxLength(512);

        builder.Property(item => item.OriginalFileName)
            .HasMaxLength(512);

        builder.Property(item => item.ContentType)
            .HasMaxLength(200);

        builder.Property(item => item.ExternalUrl)
            .HasMaxLength(2048);

        builder.Property(item => item.Description)
            .HasColumnType("text");

        // STOR-44 Phase 1: the student's per-item drill-down opt-in. The
        // migration back-fills existing rows to `false` via a SQL DEFAULT,
        // matching the documented "off by default" semantics — a student who
        // hasn't reviewed this control yet has nothing exposed to employers.
        builder.Property(item => item.ShareOriginalWithEmployers)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(item => item.CreatedAt)
            .IsRequired();

        // Supports the global query filter installed in WriteDbContext.OnModelCreating.
        // Same rationale as JobConfiguration: every read against PortfolioItems gets
        // WHERE "AccountId" = ambient, and this index is what keeps that hot.
        builder.HasIndex(item => item.AccountId);

        builder.HasOne(item => item.Account)
            .WithMany()
            .HasForeignKey(item => item.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
