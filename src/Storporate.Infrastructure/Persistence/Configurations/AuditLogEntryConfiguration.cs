using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="AuditLogEntry"/>. See <see cref="UserConfiguration"/> for
/// why this is a standalone configuration class rather than inline in
/// <see cref="WriteDbContext.OnModelCreating"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>SequenceNumber as <see cref="PropertyBuilder.UseIdentityAlwaysColumn"/>.</b> The hash
/// chain depends on a strict, gap-free ordering of rows per chain; an identity-always
/// sequence (allocated server-side, monotonic, no manual values) is what guarantees that.
/// The unique index on <c>SequenceNumber</c> is belt-and-braces — the sequence itself is
/// already unique, but the index also supports the ORDER-BY-DESC LIMIT-1 chain-tip read.
/// </para>
/// <para>
/// <b>Composite (AccountId, SequenceNumber) index.</b> Supports the same chain-tip read
/// scoped by <c>AccountId</c>, and supports the future admin query endpoint's per-account
/// pagination. Without this index the chain-tip lookup degenerates to a sequential scan
/// once a long-lived account accumulates rows.
/// </para>
/// <para>
/// <b>No <see cref="IAccountScoped"/>.</b> <see cref="AuditLogEntry"/> intentionally does not
/// implement <see cref="IAccountScoped"/> (see its class remarks) and therefore does not
/// participate in the global query filter / save-time interceptor / RLS policy pipeline.
/// Access control is enforced at the endpoint level by <see cref="SharedKernel.Authorization.Permissions.SecurityGovernance.ViewAuditLog"/>
/// in Phase 3.
/// </para>
/// </remarks>
public sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("AuditLogEntries");

        builder.HasKey(entry => entry.Id);

        // Server-allocated, monotonic, gap-free identity-always sequence. The unique index
        // mirrors the sequence's uniqueness for query-plan purposes and lets the chain-tip
        // ORDER BY SequenceNumber DESC LIMIT 1 hit an index rather than sort.
        builder.Property(entry => entry.SequenceNumber)
            .UseIdentityAlwaysColumn();

        builder.Property(entry => entry.AccountId);
        builder.Property(entry => entry.ActorUserId);

        builder.Property(entry => entry.Action)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(entry => entry.ResourceType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(entry => entry.ResourceId)
            .HasMaxLength(200);

        builder.Property(entry => entry.IpAddress)
            .HasMaxLength(64);

        builder.Property(entry => entry.UserAgent)
            .HasMaxLength(AuditLogEntry.UserAgentMaxLength);

        builder.Property(entry => entry.MetadataJson)
            .HasColumnType("jsonb");

        builder.Property(entry => entry.CreatedAt)
            .IsRequired();

        builder.Property(entry => entry.Hash)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(entry => entry.PreviousHash)
            .HasMaxLength(64);

        // Composite (AccountId, SequenceNumber) index — supports chain-tip lookup and
        // future admin-query pagination. Includes SequenceNumber DESC implicitly via the
        // ORDER BY + index direction chosen by Postgres' planner.
        builder.HasIndex(entry => new { entry.AccountId, entry.SequenceNumber });

        // Unique on SequenceNumber alone — belt-and-braces; the sequence is already unique
        // but indexing it speeds up global ordering queries (e.g. the cross-chain
        // SequenceNumber assignments used during integrity verification).
        builder.HasIndex(entry => entry.SequenceNumber)
            .IsUnique();
    }
}
