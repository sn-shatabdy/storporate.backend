using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="Session"/>. See <see cref="UserConfiguration"/> for
/// why this is a standalone configuration class rather than inline in
/// <see cref="WriteDbContext.OnModelCreating"/>.</summary>
public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("Sessions");

        builder.HasKey(session => session.Id);

        builder.Property(session => session.HashedRefreshToken)
            .IsRequired();

        builder.Property(session => session.ExpiresAt)
            .IsRequired();

        builder.Property(session => session.CreatedAt)
            .IsRequired();

        builder.Property(session => session.UpdatedAt)
            .IsRequired();

        builder.HasIndex(session => session.HashedRefreshToken)
            .IsUnique();

        builder.HasIndex(session => session.FamilyId);

        builder.HasOne(session => session.User)
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-referencing rotation link (see Session's own doc comment). Restrict rather than
        // Cascade so deleting one session in a chain can never cascade-delete the rest of the
        // chain out from under the reuse-detection logic.
        builder.HasOne(session => session.ReplacedBySession)
            .WithMany()
            .HasForeignKey(session => session.ReplacedBySessionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
