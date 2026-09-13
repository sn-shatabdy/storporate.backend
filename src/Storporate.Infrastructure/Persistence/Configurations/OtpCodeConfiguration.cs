using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="OtpCode"/>. See <see cref="UserConfiguration"/> for
/// why this is a standalone configuration class rather than inline in
/// <see cref="WriteDbContext.OnModelCreating"/>.</summary>
public sealed class OtpCodeConfiguration : IEntityTypeConfiguration<OtpCode>
{
    public void Configure(EntityTypeBuilder<OtpCode> builder)
    {
        builder.ToTable("OtpCodes");

        builder.HasKey(otpCode => otpCode.Id);

        builder.Property(otpCode => otpCode.Email)
            .IsRequired();

        builder.Property(otpCode => otpCode.HashedCode)
            .IsRequired();

        builder.Property(otpCode => otpCode.ExpiresAt)
            .IsRequired();

        builder.Property(otpCode => otpCode.AttemptCount)
            .IsRequired();

        builder.Property(otpCode => otpCode.MaxAttempts)
            .IsRequired();

        builder.Property(otpCode => otpCode.CreatedAt)
            .IsRequired();

        // Not unique: an email can accumulate multiple OtpCode rows over time (one per request);
        // lookups filter to the most recent unconsumed, unexpired row for the email.
        builder.HasIndex(otpCode => otpCode.Email);
    }
}
