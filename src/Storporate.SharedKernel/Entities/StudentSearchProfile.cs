namespace Storporate.SharedKernel.Entities;

/// <summary>
/// One student's "let employers find me" opt-in profile. STOR-43 Phase 1:
/// the student toggles <see cref="IsSearchable"/>, supplies a required
/// <see cref="DisplayName"/> plus optional <see cref="Headline"/>,
/// <see cref="University"/>, <see cref="FieldOfStudy"/>, and <see cref="StudyYear"/>,
/// and chooses per-field visibility via the four <c>Show*</c> flags. Exactly
/// one row exists per student (the configuration enforces a unique index on
/// <see cref="AccountId"/>); the GET endpoint returns safe defaults when the
/// student has never toggled the feature.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implements <see cref="IAccountScoped"/>.</b> Only the owning student ever
/// reads or writes their own row — the EF Core global query filter restricts
/// reads to the ambient account and the <c>account_scoped</c> PostgreSQL
/// row-level security policy (installed by <c>AddTalentSearchTables</c>) does
/// the same at the database layer. The mirror non-tenant table
/// (<see cref="TalentIndexEntry"/>) is what an Organization actually queries;
/// this row is the student's opt-in flag plus the profile fields they want
/// exposed.
/// </para>
/// <para>
/// <b>Why every field has its own show-flag.</b> The plan calls out that
/// University and FieldOfStudy are <em>self-reported</em> and unverified; the
/// Show flags let a student share them with employers without forcing the
/// share, which matches the "student chooses what to share" decision in the
/// plan's confirmed-decisions list. All four flags default to <c>true</c> so a
/// student who opts in with a minimal request still gets a useful preview.
/// </para>
/// <para>
/// <b>Timestamp shape.</b> <see cref="OptedInAt"/> records the first time the
/// student flipped the toggle to <c>true</c> and is preserved across subsequent
/// PUTs so the audit trail can answer "how long has this student been
/// searchable?" without diffing the audit log itself.
/// <see cref="UpdatedAt"/> advances on every PUT — including field-only edits
/// that don't touch <see cref="IsSearchable"/> — so the FE can detect a save
/// round-trip with one equality check.
/// </para>
/// </remarks>
public sealed class StudentSearchProfile : IAccountScoped
{
    /// <summary>The owning <see cref="User.Id"/>. See class remarks.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The primary key. Mirrors the existing
    /// <see cref="PortfolioItem.Id"/> shape so callers can reference the row
    /// uniformly; the unique index is on <see cref="AccountId"/> rather than
    /// this id because the entity is one-row-per-student.</summary>
    public Guid Id { get; set; }

    /// <summary>True iff the student wants to appear in employer search results.
    /// When false, the mirror <see cref="TalentIndexEntry"/> row is removed in
    /// the same request as the PUT (see
    /// <c>Storporate.Modules.DiscoveryHiring.UpdateSearchableProfileHandler</c>).</summary>
    public bool IsSearchable { get; set; }

    /// <summary>The display name shown to employers. Required (the column is
    /// non-nullable) but may be the empty string while <see cref="IsSearchable"/>
    /// is false; the validator rejects a <c>true</c> PUT with a blank name so
    /// the row can never be simultaneously searchable and anonymous.</summary>
    public required string DisplayName { get; set; }

    /// <summary>One-line self-description shown to employers when
    /// <see cref="ShowHeadline"/> is true.</summary>
    public string? Headline { get; set; }

    /// <summary>Self-reported university. Rendered with the "Self-reported"
    /// label in the UI per the plan's confirmed-decisions list.</summary>
    public string? University { get; set; }

    /// <summary>Self-reported field of study (e.g. "Computer Science"). Also
    /// carries the "Self-reported" label in the UI.</summary>
    public string? FieldOfStudy { get; set; }

    /// <summary>Current year of study, 1-8 (the validator enforces this range;
    /// null when the student chose not to supply it).</summary>
    public int? StudyYear { get; set; }

    /// <summary>True iff the student wants employers to see <see cref="Headline"/>.</summary>
    public bool ShowHeadline { get; set; } = true;

    /// <summary>True iff the student wants employers to see <see cref="University"/>.</summary>
    public bool ShowUniversity { get; set; } = true;

    /// <summary>True iff the student wants employers to see <see cref="FieldOfStudy"/>.</summary>
    public bool ShowFieldOfStudy { get; set; } = true;

    /// <summary>True iff the student wants employers to see <see cref="StudyYear"/>.</summary>
    public bool ShowStudyYear { get; set; } = true;

    /// <summary>UTC timestamp of the most recent false→true transition of
    /// <see cref="IsSearchable"/>. Set once on the first opt-in and preserved
    /// across subsequent PUTs.</summary>
    public DateTimeOffset? OptedInAt { get; set; }

    /// <summary>UTC timestamp of the most recent PUT to this row. Advances on
    /// every write, including field-only edits that don't touch
    /// <see cref="IsSearchable"/>.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
