using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="TalentIndexEntry"/>. STOR-43 Phase 1:
/// GLOBAL table — not <see cref="IAccountScoped"/>, no <c>AccountId</c>
/// column, no RLS policy. Read by every Organization via the Phase 2 search
/// endpoint; written only by the
/// <c>RefreshTalentIndexEntryProcessor</c> background job (Phase 1) or
/// removed by the opt-out PUT handler (Phase 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Embedding column is intentionally absent from the model.</b> The
/// <c>vector(768)</c> column is added by the
/// <c>AddTalentSearchTables</c> migration's raw-SQL block — EF never knows
/// about it, so the InMemory test suite keeps booting without a Postgres-only
/// type. The repository implementation branches on
/// <c>WriteDbContext.Database.ProviderName</c> to choose between the raw-SQL
/// pgvector path (production) and an in-memory dictionary store (tests).
/// </para>
/// <para>
/// <b>ItemsJson / SearchText length caps.</b> The <see cref="TalentIndexEntry.ItemsJson"/>
/// snapshot grows with the number of analyzed items (each item carries its
/// own skills array); <c>text</c> is unbounded in Postgres. <see cref="TalentIndexEntry.SearchText"/>
/// is capped by the processor at 8 000 characters; we map the column to
/// <c>text</c> so the cap is application-side rather than schema-side (a
/// future change to the cap is a single constant edit, not a migration).
/// </para>
/// </remarks>
public sealed class TalentIndexEntryConfiguration : IEntityTypeConfiguration<TalentIndexEntry>
{
    /// <summary>Upper bound on <see cref="TalentIndexEntry.DisplayName"/> —
    /// matches the <c>StudentSearchProfileConfiguration.DisplayNameMaxLength</c>
    /// constant so the processor never produces a value that won't fit.</summary>
    public const int DisplayNameMaxLength = 80;

    /// <summary>Upper bound on the per-entry content hash (SHA-256 hex = 64
    /// characters). Picked to fit the hash verbatim with no truncation.</summary>
    public const int ContentHashMaxLength = 64;

    public void Configure(EntityTypeBuilder<TalentIndexEntry> builder)
    {
        builder.ToTable("TalentIndexEntries");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.StudentAccountId)
            .IsRequired();

        builder.Property(entry => entry.DisplayName)
            .IsRequired()
            .HasMaxLength(DisplayNameMaxLength);

        builder.Property(entry => entry.Headline)
            .HasMaxLength(120);

        builder.Property(entry => entry.University)
            .HasMaxLength(120);

        builder.Property(entry => entry.FieldOfStudy)
            .HasMaxLength(120);

        builder.Property(entry => entry.StudyYear);

        builder.Property(entry => entry.ItemsJson)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(entry => entry.SearchText)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(entry => entry.ContentHash)
            .IsRequired()
            .HasMaxLength(ContentHashMaxLength);

        builder.Property(entry => entry.UpdatedAt)
            .IsRequired();

        // Upsert key for the refresh processor — exactly one row per
        // opted-in student. The Phase 2 search endpoint queries this column
        // via the global index, so the unique constraint is also the lookup
        // index for the delete-by-student path.
        builder.HasIndex(entry => entry.StudentAccountId)
            .IsUnique();
    }
}
