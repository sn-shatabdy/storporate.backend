using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Job"/>. Extracted from the inline mapping that previously
/// lived in <see cref="WriteDbContext.OnModelCreating"/> so that <see cref="Job"/> matches the
/// standalone-configuration style established for <see cref="User"/>,
/// <see cref="OtpCode"/>, and <see cref="Session"/> in STOR-61.
/// </summary>
/// <remarks>
/// STOR-62 Phase 1: <see cref="Job"/> is the first entity in the model to implement
/// <see cref="IAccountScoped"/>. <see cref="EntityTypeBuilder{TEntity}.HasOne"/> wires the
/// <c>AccountId</c> foreign key to <c>Users.Id</c>; <see cref="DeleteBehavior.Restrict"/>
/// is chosen so that a <see cref="User"/> cannot be deleted while they still own
/// account-scoped <c>Job</c> rows (a delete attempt will fail loudly at the database rather
/// than silently cascading job rows away). The index on <c>AccountId</c> supports the global
/// query filter the migration of Phase 4 will install, which filters <c>WHERE "AccountId" =
/// ambient</c> on every read.
/// </remarks>
public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("Jobs");

        builder.HasKey(job => job.Id);

        builder.Property(job => job.Type)
            .IsRequired();

        builder.Property(job => job.PayloadJson)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(job => job.Status)
            .IsRequired();

        builder.Property(job => job.ErrorMessage)
            .HasColumnType("text");

        builder.Property(job => job.AccountId)
            .IsRequired();

        builder.Property(job => job.CreatedAt)
            .IsRequired();

        builder.Property(job => job.UpdatedAt)
            .IsRequired();

        builder.HasIndex(job => job.Status);

        // Supports the global query filter the Phase 4 interceptor story installs.
        builder.HasIndex(job => job.AccountId);

        builder.HasOne(job => job.Account)
            .WithMany()
            .HasForeignKey(job => job.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}