using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="StudentFeedEntry"/>. STOR-40 Phase 1:
/// tenant table. The match pipeline is the only writer; the per-account
/// feed page is the only reader.
/// </summary>
public sealed class StudentFeedEntryConfiguration : IEntityTypeConfiguration<StudentFeedEntry>
{
    public void Configure(EntityTypeBuilder<StudentFeedEntry> builder)
    {
        builder.ToTable("StudentFeedEntries");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.AccountId)
            .IsRequired();

        builder.Property(entry => entry.FeedItemId)
            .IsRequired();

        builder.Property(entry => entry.Reason)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(entry => entry.MatchedAt)
            .IsRequired();

        builder.HasIndex(entry => entry.AccountId);

        // A student can't be matched to the same FeedItem twice — the
        // match job's "upsert by (AccountId, FeedItemId)" path needs a
        // unique key. Prevents the table from filling up with duplicates
        // when the match job runs every interval.
        builder.HasIndex(entry => new { entry.AccountId, entry.FeedItemId })
            .IsUnique();

        builder.HasOne(entry => entry.Account)
            .WithMany()
            .HasForeignKey(entry => entry.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(entry => entry.FeedItem)
            .WithMany()
            .HasForeignKey(entry => entry.FeedItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}