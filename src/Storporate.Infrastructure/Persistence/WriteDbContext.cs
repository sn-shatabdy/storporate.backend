using Microsoft.EntityFrameworkCore;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence;

/// <summary>
/// The primary write-side EF Core context. Named "WriteDbContext" (rather than a generic
/// "AppDbContext") to leave room for a dedicated read-side context later without a rename.
/// </summary>
public sealed class WriteDbContext(DbContextOptions<WriteDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("Jobs");

            entity.HasKey(job => job.Id);

            entity.Property(job => job.Type)
                .IsRequired();

            entity.Property(job => job.PayloadJson)
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(job => job.Status)
                .IsRequired();

            entity.Property(job => job.ErrorMessage)
                .HasColumnType("text");

            entity.Property(job => job.CreatedAt)
                .IsRequired();

            entity.Property(job => job.UpdatedAt)
                .IsRequired();

            entity.HasIndex(job => job.Status);
        });
    }
}
