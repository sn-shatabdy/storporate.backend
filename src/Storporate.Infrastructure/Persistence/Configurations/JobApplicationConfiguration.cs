using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="JobApplication"/> (STOR-67). Non-tenant table; one application per student per posting.</summary>
public sealed class JobApplicationConfiguration : IEntityTypeConfiguration<JobApplication>
{
    public void Configure(EntityTypeBuilder<JobApplication> builder)
    {
        builder.ToTable("JobApplications");

        builder.HasKey(application => application.Id);

        builder.Property(application => application.JobPostingId).IsRequired();
        builder.Property(application => application.StudentAccountId).IsRequired();
        builder.Property(application => application.Status).IsRequired().HasMaxLength(20);
        builder.Property(application => application.CreatedAt).IsRequired();
        builder.Property(application => application.UpdatedAt).IsRequired();
        builder.Property(application => application.StatusChangedAt).IsRequired();
        builder.Property(application => application.SnapshotJson).IsRequired().HasColumnType("text");

        builder.HasOne(application => application.JobPosting)
            .WithMany()
            .HasForeignKey(application => application.JobPostingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(application => application.Student)
            .WithMany()
            .HasForeignKey(application => application.StudentAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(application => new { application.JobPostingId, application.StudentAccountId }).IsUnique();
        builder.HasIndex(application => application.StudentAccountId);
        builder.HasIndex(application => new { application.JobPostingId, application.CreatedAt });

        // Postgres xmin is a system column that increments on every UPDATE. Mapping it as a
        // concurrency token gives read-modify-write protection at the row level: the second
        // writer's SaveChanges throws DbUpdateConcurrencyException when its loaded token no
        // longer matches the live row. The mapping is harmless under the InMemory provider
        // (EF never reads xmin there) — endpoint tests still pass; live Postgres tests prove
        // the second-writer path on status changes (STOR-67 Phase 1).
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
