using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="TalentSearchRequest"/>. STOR-43 Phase 2:
/// tenant table. The <c>(AccountId, Status)</c> composite index covers the
/// POST handler's "one pending search per Organization" busy check without
/// a full table scan, and the standalone <c>AccountId</c> index covers the
/// (rarer) cross-account-admin read path. <c>ResultJson</c> uses the
/// <c>jsonb</c> column type so future server-side reads can deserialize
/// directly; today the field is opaque to EF and only the response-shape
/// handler reads it.
/// </summary>
public sealed class TalentSearchRequestConfiguration : IEntityTypeConfiguration<TalentSearchRequest>
{
    public void Configure(EntityTypeBuilder<TalentSearchRequest> builder)
    {
        builder.ToTable("TalentSearchRequests");

        builder.HasKey(request => request.Id);

        builder.Property(request => request.AccountId)
            .IsRequired();

        builder.Property(request => request.QueryText)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(request => request.Status)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(request => request.ResultJson)
            .HasColumnType("jsonb");

        builder.Property(request => request.ErrorCode)
            .HasMaxLength(100);

        builder.Property(request => request.CreatedAt)
            .IsRequired();

        builder.Property(request => request.CompletedAt);

        builder.HasIndex(request => new { request.AccountId, request.Status });
        builder.HasIndex(request => request.AccountId);

        builder.HasOne(request => request.Account)
            .WithMany()
            .HasForeignKey(request => request.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
