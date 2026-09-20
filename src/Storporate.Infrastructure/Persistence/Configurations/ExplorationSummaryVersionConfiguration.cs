using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="ExplorationSummaryVersion"/>. STOR-40
/// Phase 1: tenant table; the gaps / suggestions stay as JSON blobs because
/// they are free-form, always read together, and never queried individually
/// (see the plan's data-model section and the entity doc comment).
/// </summary>
public sealed class ExplorationSummaryVersionConfiguration : IEntityTypeConfiguration<ExplorationSummaryVersion>
{
    public void Configure(EntityTypeBuilder<ExplorationSummaryVersion> builder)
    {
        builder.ToTable("ExplorationSummaryVersions");

        builder.HasKey(version => version.Id);

        builder.Property(version => version.AccountId)
            .IsRequired();

        builder.Property(version => version.ExplorationId)
            .IsRequired();

        builder.Property(version => version.VersionNumber)
            .IsRequired();

        builder.Property(version => version.GapsJson)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(version => version.SuggestionsJson)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(version => version.ChangeNote)
            .HasColumnType("text");

        builder.Property(version => version.CreatedAt)
            .IsRequired();

        builder.HasIndex(version => version.AccountId);

        // "Latest version for exploration X" — both the live summary read
        // and the compare job's "read both latest summaries" path hit this.
        // Unique on (ExplorationId, VersionNumber) so the application-side
        // MAX(...)+1 dance can never produce two rows with the same number.
        builder.HasIndex(version => new { version.ExplorationId, version.VersionNumber })
            .IsUnique();

        builder.HasOne(version => version.Account)
            .WithMany()
            .HasForeignKey(version => version.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(version => version.Exploration)
            .WithMany()
            .HasForeignKey(version => version.ExplorationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}