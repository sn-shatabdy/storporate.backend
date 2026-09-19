using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="StudentContextNote"/>. STOR-40 Phase 1:
/// tenant table; <see cref="StudentContextNote.ExplorationId"/> is nullable
/// (a "general background" note has no originating exploration). The FK
/// uses <see cref="DeleteBehavior.Restrict"/> so the application's hard-delete
/// pipeline must clear or reassign context notes first — same rationale as
/// <c>PortfolioSkillFindingConfiguration</c> and <c>JobConfiguration</c>.
/// </summary>
public sealed class StudentContextNoteConfiguration : IEntityTypeConfiguration<StudentContextNote>
{
    public void Configure(EntityTypeBuilder<StudentContextNote> builder)
    {
        builder.ToTable("StudentContextNotes");

        builder.HasKey(note => note.Id);

        builder.Property(note => note.AccountId)
            .IsRequired();

        // Nullable FK: a note can be "general background" (no originating
        // exploration) — see the entity doc comment.
        builder.Property(note => note.ExplorationId)
            .IsRequired(false);

        builder.Property(note => note.Text)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(note => note.CreatedAt)
            .IsRequired();

        builder.HasIndex(note => note.AccountId);

        // "Notes shared across this student's explorations" — the advisor
        // pipeline joins on AccountId, not on ExplorationId, so the index
        // is keyed off AccountId alone. (ExplorationId is incidental data.)
        builder.HasIndex(note => new { note.AccountId, note.CreatedAt });

        builder.HasOne(note => note.Account)
            .WithMany()
            .HasForeignKey(note => note.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(note => note.Exploration)
            .WithMany()
            .HasForeignKey(note => note.ExplorationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}