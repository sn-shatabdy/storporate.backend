using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>EF Core mapping for <see cref="SponsorshipRequest"/> (STOR-72). Non-tenant: each side is scoped explicitly by its owner account column. The goal set link is SET NULL because a company may delete a goal set later.</summary>
public sealed class SponsorshipRequestConfiguration : IEntityTypeConfiguration<SponsorshipRequest>
{
    public void Configure(EntityTypeBuilder<SponsorshipRequest> builder)
    {
        builder.ToTable("SponsorshipRequests");

        builder.HasKey(request => request.Id);

        builder.Property(request => request.ClubOwnerAccountId).IsRequired();
        builder.Property(request => request.CompanyOwnerAccountId).IsRequired();
        builder.Property(request => request.ClubProfileId).IsRequired();
        builder.Property(request => request.GoalSetId);

        builder.Property(request => request.ClubName).IsRequired().HasMaxLength(150);
        builder.Property(request => request.University).IsRequired().HasMaxLength(150);
        builder.Property(request => request.CompanyName).IsRequired().HasMaxLength(150);
        builder.Property(request => request.GoalName).IsRequired().HasMaxLength(100);
        builder.Property(request => request.EventTitle).IsRequired().HasMaxLength(150);
        builder.Property(request => request.EventDate).HasColumnType("date");
        builder.Property(request => request.EventDescription).IsRequired().HasColumnType("text");
        builder.Property(request => request.Ask).IsRequired().HasColumnType("text");
        builder.Property(request => request.AmountRequested);
        builder.Property(request => request.Offer).IsRequired().HasColumnType("text");

        builder.Property(request => request.Status).IsRequired().HasMaxLength(20);
        builder.Property(request => request.DecisionNote).HasColumnType("text");
        builder.Property(request => request.OutcomeNote).HasColumnType("text");
        builder.Property(request => request.AgreedAmount);

        builder.Property(request => request.CreatedAt).IsRequired();
        builder.Property(request => request.UpdatedAt).IsRequired();
        builder.Property(request => request.ViewedAt);
        builder.Property(request => request.DecidedAt);
        builder.Property(request => request.CompletedAt);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(request => request.ClubOwnerAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(request => request.CompanyOwnerAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ClubProfile>()
            .WithMany()
            .HasForeignKey(request => request.ClubProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SponsorshipGoalSet>()
            .WithMany()
            .HasForeignKey(request => request.GoalSetId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(request => new { request.ClubOwnerAccountId, request.Status });
        builder.HasIndex(request => new { request.CompanyOwnerAccountId, request.Status });
        builder.HasIndex(request => request.GoalSetId);
    }
}
