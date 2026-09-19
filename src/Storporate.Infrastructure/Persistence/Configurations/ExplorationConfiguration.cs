using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Exploration"/>. STOR-40 Phase 1: tenant
/// table, implements <see cref="IAccountScoped"/> so the global query filter
/// installed in <see cref="WriteDbContext.OnModelCreating"/> scopes reads to
/// the ambient account. The companion
/// <c>AddStudentGrowthRowLevelSecurity</c> migration adds the matching
/// Postgres RLS policy.
/// </summary>
public sealed class ExplorationConfiguration : IEntityTypeConfiguration<Exploration>
{
    public void Configure(EntityTypeBuilder<Exploration> builder)
    {
        builder.ToTable("Explorations");

        builder.HasKey(exploration => exploration.Id);

        builder.Property(exploration => exploration.AccountId)
            .IsRequired();

        builder.Property(exploration => exploration.Title)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(exploration => exploration.Status)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(exploration => exploration.LastError)
            .HasColumnType("text");

        builder.Property(exploration => exploration.CreatedAt)
            .IsRequired();

        builder.Property(exploration => exploration.UpdatedAt)
            .IsRequired();

        // Global query filter reads WHERE "AccountId" = ambient. This index
        // matches PortfolioSkillFindingConfiguration's rationale verbatim.
        builder.HasIndex(exploration => exploration.AccountId);

        // The "list this student's explorations, newest activity first" path
        // hits (AccountId, UpdatedAt DESC) — the composite index makes it
        // a single index range scan rather than a sort over a filtered set.
        builder.HasIndex(exploration => new { exploration.AccountId, exploration.UpdatedAt });

        builder.HasOne(exploration => exploration.Account)
            .WithMany()
            .HasForeignKey(exploration => exploration.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}