using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="TalentSearchRequest"/>. STOR-43 Phase 2:
/// tenant table. The partial unique index on <c>AccountId</c> WHERE
/// <c>Status = 'Pending'</c> backs the busy-state race guard so two
/// concurrent <c>POST /api/discovery/talent-searches</c> calls cannot both
/// insert a Pending row for the same Organization; the unique-violation
/// <c>23505</c> is caught and remapped to
/// <see cref="TalentSearchBusyException"/> (HTTP 409). The index name in
/// the EF model (<c>IX_TalentSearchRequests_AccountId</c>) is the
/// post-rename name; the original install migration
/// <c>AddTalentSearchPendingUniqueIndex</c> created it under the legacy
/// name <c>UX_TalentSearchRequests_AccountId_Pending</c>, and the
/// follow-up migration <c>SyncTalentSearchRequestIndexes</c> renames it
/// idempotently so future designer-driven migrations see a single
/// consistent name. Completed / Failed rows are unrestricted, so any
/// number of past searches accumulate per Organization.
/// <c>ResultJson</c> uses the <c>jsonb</c> column type so future
/// server-side reads can deserialize directly; today the field is
/// opaque to EF and only the response-shape handler reads it.
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

        // Partial unique index: only one Pending row per AccountId. Also
        // serves the cross-account admin read path — Postgres can still use
        // a partial unique index for `WHERE "AccountId" = $1` queries.
        builder.HasIndex(request => request.AccountId)
            .HasDatabaseName("IX_TalentSearchRequests_AccountId")
            .HasFilter("\"Status\" = 'Pending'")
            .IsUnique();

        builder.HasOne(request => request.Account)
            .WithMany()
            .HasForeignKey(request => request.AccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
