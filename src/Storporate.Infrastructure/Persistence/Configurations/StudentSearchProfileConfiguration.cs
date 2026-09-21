using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="StudentSearchProfile"/>. STOR-43 Phase 1:
/// the student-side opt-in row. Implements
/// <see cref="IAccountScoped"/> so the global query filter (installed in
/// <c>WriteDbContext.OnModelCreating</c>) restricts reads to the caller's
/// account automatically; the matching <c>account_scoped</c> Postgres
/// row-level security policy is installed by the
/// <c>AddTalentSearchTables</c> migration's follow-up DDL block.
/// </summary>
/// <remarks>
/// <b>One row per student.</b> The unique index on <see cref="StudentSearchProfile.AccountId"/>
/// is what enforces this — every student has zero or one opt-in profile, no
/// matter how many PUTs they make. <see cref="StudentSearchProfile.Id"/> still
/// exists as the primary key so the audit log can reference a row by its
/// server-assigned id (the audit writer stamps the resource id as the
/// <see cref="StudentSearchProfile.Id"/> value, not the
/// <see cref="StudentSearchProfile.AccountId"/>).
/// </remarks>
public sealed class StudentSearchProfileConfiguration : IEntityTypeConfiguration<StudentSearchProfile>
{
    /// <summary>Upper bound on <see cref="StudentSearchProfile.DisplayName"/> —
    /// matches the validator's <c>display_name_too_long</c> error code so a
    /// name that survives validation is guaranteed to fit the column.</summary>
    public const int DisplayNameMaxLength = 80;

    /// <summary>Upper bound on <see cref="StudentSearchProfile.Headline"/>,
    /// <see cref="StudentSearchProfile.University"/>, and
    /// <see cref="StudentSearchProfile.FieldOfStudy"/>.</summary>
    public const int ProfileTextMaxLength = 120;

    public void Configure(EntityTypeBuilder<StudentSearchProfile> builder)
    {
        builder.ToTable("StudentSearchProfiles");

        builder.HasKey(profile => profile.Id);

        builder.Property(profile => profile.AccountId)
            .IsRequired();

        builder.Property(profile => profile.IsSearchable)
            .IsRequired();

        builder.Property(profile => profile.DisplayName)
            .IsRequired()
            .HasMaxLength(DisplayNameMaxLength);

        builder.Property(profile => profile.Headline)
            .HasMaxLength(ProfileTextMaxLength);

        builder.Property(profile => profile.University)
            .HasMaxLength(ProfileTextMaxLength);

        builder.Property(profile => profile.FieldOfStudy)
            .HasMaxLength(ProfileTextMaxLength);

        builder.Property(profile => profile.StudyYear);

        builder.Property(profile => profile.ShowHeadline)
            .IsRequired();

        builder.Property(profile => profile.ShowUniversity)
            .IsRequired();

        builder.Property(profile => profile.ShowFieldOfStudy)
            .IsRequired();

        builder.Property(profile => profile.ShowStudyYear)
            .IsRequired();

        builder.Property(profile => profile.OptedInAt);

        builder.Property(profile => profile.UpdatedAt)
            .IsRequired();

        // Upsert key for the PUT endpoint: one row per account. The refresh
        // processor uses the same AccountId lookup, so the index serves both
        // the synchronous endpoint path and the background job path.
        builder.HasIndex(profile => profile.AccountId)
            .IsUnique();
    }
}
