using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="ExplorationComparison"/>. STOR-40 Phase 1:
/// tenant table. The two exploration FKs both use
/// <see cref="DeleteBehavior.Restrict"/> so deleting an Exploration that's
/// still referenced by a comparison fails loudly at the database — the
/// application is responsible for clearing comparisons first.
/// </summary>
public sealed class ExplorationComparisonConfiguration : IEntityTypeConfiguration<ExplorationComparison>
{
    public void Configure(EntityTypeBuilder<ExplorationComparison> builder)
    {
        builder.ToTable("ExplorationComparisons");

        builder.HasKey(comparison => comparison.Id);

        builder.Property(comparison => comparison.AccountId)
            .IsRequired();

        builder.Property(comparison => comparison.FirstExplorationId)
            .IsRequired();

        builder.Property(comparison => comparison.SecondExplorationId)
            .IsRequired();

        builder.Property(comparison => comparison.Status)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(comparison => comparison.ResultText)
            .HasColumnType("text");

        builder.Property(comparison => comparison.CreatedAt)
            .IsRequired();

        builder.HasIndex(comparison => comparison.AccountId);

        // "List this student's comparisons, newest first" — matches the
        // (AccountId, UpdatedAt)-style shape used by Exploration, except
        // comparison rows are immutable so CreatedAt is the only ordering
        // key that matters.
        builder.HasIndex(comparison => new { comparison.AccountId, comparison.CreatedAt });

        builder.HasOne(comparison => comparison.Account)
            .WithMany()
            .HasForeignKey(comparison => comparison.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(comparison => comparison.FirstExploration)
            .WithMany()
            .HasForeignKey(comparison => comparison.FirstExplorationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(comparison => comparison.SecondExploration)
            .WithMany()
            .HasForeignKey(comparison => comparison.SecondExplorationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}