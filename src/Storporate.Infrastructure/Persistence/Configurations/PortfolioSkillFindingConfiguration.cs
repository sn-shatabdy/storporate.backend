using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="PortfolioSkillFinding"/>. Follows the standalone-
/// configuration style established by <see cref="JobConfiguration"/> and
/// <see cref="PortfolioItemConfiguration"/> — table naming + index on <c>AccountId</c> +
/// <see cref="DeleteBehavior.Restrict"/> foreign key to <c>Users.Id</c>, so the global
/// query filter installed by <see cref="WriteDbContext.OnModelCreating"/> resolves against
/// the same index <see cref="JobConfiguration"/> uses, and STOR-62's three-layer isolation
/// pipeline (global query filter, save-time interceptor, Postgres RLS policy) applies to
/// <see cref="PortfolioSkillFinding"/> rows with zero per-entity extra work.
/// </summary>
/// <remarks>
/// STOR-38 Phase 1 — schema-only POCO; populated by the background worker introduced in
/// Phase 2 and read by the per-item analysis endpoint introduced in Phase 3. The FK to
/// <see cref="PortfolioItem"/> uses <see cref="DeleteBehavior.Restrict"/> (matching
/// <see cref="JobConfiguration"/>'s FK choice) so the application's hard-delete pipeline
/// can't silently orphan findings: the worker clears findings before the parent item can
/// be deleted, and any future race that tries to delete an item with live findings fails
/// loudly at the database.
/// </remarks>
public sealed class PortfolioSkillFindingConfiguration : IEntityTypeConfiguration<PortfolioSkillFinding>
{
    public void Configure(EntityTypeBuilder<PortfolioSkillFinding> builder)
    {
        builder.ToTable("PortfolioSkillFindings");

        builder.HasKey(finding => finding.Id);

        builder.Property(finding => finding.AccountId)
            .IsRequired();

        builder.Property(finding => finding.PortfolioItemId)
            .IsRequired();

        builder.Property(finding => finding.SkillName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(finding => finding.ConfidenceBand)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(finding => finding.Explanation)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(finding => finding.CreatedAt)
            .IsRequired();

        // Supports the global query filter installed in WriteDbContext.OnModelCreating.
        // Same rationale as JobConfiguration: every read against PortfolioSkillFindings
        // gets WHERE "AccountId" = ambient, and this index is what keeps that hot.
        builder.HasIndex(finding => finding.AccountId);

        // Supports the per-item analysis lookup in Phase 3
        // (GET /api/portfolio/items/{id}/analysis) and the worker's "delete prior findings
        // for this item before writing the new set" path. A composite (PortfolioItemId,
        // CreatedAt) would also serve the per-item ordered read, but a plain
        // PortfolioItemId index is what the worker hit first; the per-item query in Phase 3
        // sorts in-process once the index has narrowed the row set to the item's findings.
        builder.HasIndex(finding => finding.PortfolioItemId);

        builder.HasOne(finding => finding.Account)
            .WithMany()
            .HasForeignKey(finding => finding.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(finding => finding.PortfolioItem)
            .WithMany()
            .HasForeignKey(finding => finding.PortfolioItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
