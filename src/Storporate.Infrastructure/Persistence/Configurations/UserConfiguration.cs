using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="User"/>. Unlike <see cref="Job"/> (whose mapping is inline in
/// <see cref="WriteDbContext.OnModelCreating"/>), STOR-61's new entities each get their own
/// <see cref="IEntityTypeConfiguration{TEntity}"/> class, applied explicitly from
/// <see cref="WriteDbContext.OnModelCreating"/>.
/// </summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(user => user.Id);

        builder.Property(user => user.Email)
            .IsRequired();

        builder.Property(user => user.ActorType)
            .IsRequired();

        builder.Property(user => user.GoogleSubjectId);

        builder.Property(user => user.VerificationStatus)
            .IsRequired();

        builder.Property(user => user.CreatedAt)
            .IsRequired();

        builder.Property(user => user.UpdatedAt)
            .IsRequired();

        builder.HasIndex(user => user.Email)
            .IsUnique();

        // Partial unique index: only enforced once an account has actually linked Google, so
        // multiple accounts that have never signed in with Google (GoogleSubjectId == null)
        // don't collide against each other.
        builder.HasIndex(user => user.GoogleSubjectId)
            .IsUnique()
            .HasFilter("\"GoogleSubjectId\" IS NOT NULL");
    }
}
