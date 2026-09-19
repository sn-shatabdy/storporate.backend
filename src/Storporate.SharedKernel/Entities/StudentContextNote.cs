namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A short fact the advisor learned about a student — university, field of
/// study, country, clubs, competitions, etc. — that is shared across ALL of
/// that student's Explorations. Created by the advisor pipeline as it asks
/// questions and records answers; never read or written by any other module.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExplorationId"/> is nullable so a note can be "general
/// background" (no specific exploration) or "first learned during
/// exploration X". The FK uses <see cref="Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict"/>
/// (configured in <c>StudentContextNoteConfiguration</c>) so deleting an
/// Exploration that still has context notes pointing at it fails loudly at
/// the database — the application is responsible for either reassigning or
/// deleting the notes first.
/// </para>
/// </remarks>
public sealed class StudentContextNote : IAccountScoped
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public User? Account { get; set; }

    /// <summary>The exploration this note was first captured during, if any.
    /// Null for "general background" notes.</summary>
    public Guid? ExplorationId { get; set; }

    public Exploration? Exploration { get; set; }

    /// <summary>The fact itself, in the student's own phrasing. Short — the
    /// advisor pipeline emits one note per answer, never multi-sentence.</summary>
    public required string Text { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}