using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="FeedItem"/>. STOR-40 Phase 1: GLOBAL
/// table — not <see cref="IAccountScoped"/>, no <c>AccountId</c> column, no
/// RLS policy. Read by every student, written only by the global
/// feed-refresh background service (Phase 3). The unique index on
/// <see cref="FeedItem.Url"/> is the upsert key.
/// </summary>
public sealed class FeedItemConfiguration : IEntityTypeConfiguration<FeedItem>
{
    public void Configure(EntityTypeBuilder<FeedItem> builder)
    {
        builder.ToTable("FeedItems");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.SourceName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(item => item.SourceUrl)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(item => item.Url)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(item => item.Title)
            .IsRequired()
            .HasMaxLength(500);

        // Summary is hard-capped at 500 characters in the entity contract
        // — the column type is text but the application trims at write time.
        builder.Property(item => item.Summary)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(item => item.PublishedAt)
            .IsRequired(false);

        builder.Property(item => item.FetchedAt)
            .IsRequired();

        // Upsert key for the feed-refresh service. The application's
        // "INSERT ... ON CONFLICT (Url) DO UPDATE" path relies on this
        // index being unique.
        builder.HasIndex(item => item.Url)
            .IsUnique();

        // "Top-N newest items" is the hot read for the match-job candidate
        // set and the feed page. Descending on PublishedAt, then FetchedAt
        // for items without a published timestamp.
        builder.HasIndex(item => new { item.PublishedAt, item.FetchedAt });
    }
}