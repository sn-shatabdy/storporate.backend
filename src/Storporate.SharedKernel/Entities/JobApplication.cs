namespace Storporate.SharedKernel.Entities;

/// <summary>
/// STOR-67: one student's application to one job or internship posting. The application is the
/// student's own profile, captured once as <see cref="SnapshotJson"/> at apply time. Deliberately
/// NOT <see cref="IAccountScoped"/> (like <see cref="JobPosting"/>): the owning Organization must
/// read applications to its postings without touching the student's private, row-level-secured
/// tables. Scoping is enforced in the handlers via <see cref="StudentAccountId"/> and the
/// posting's owner.
/// </summary>
public sealed class JobApplication
{
    public Guid Id { get; set; }

    public Guid JobPostingId { get; set; }

    public JobPosting? JobPosting { get; set; }

    public Guid StudentAccountId { get; set; }

    public User? Student { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset StatusChangedAt { get; set; }

    /// <summary>Immutable applicant snapshot (display name, visible profile fields, items with
    /// skill bands only, fit). Never holds descriptions, file names, storage keys, URLs or reasons.</summary>
    public required string SnapshotJson { get; set; }
}

public static class JobApplicationStatuses
{
    public const string Submitted = "Submitted";
    public const string Viewed = "Viewed";
    public const string Shortlisted = "Shortlisted";
    public const string NotSelected = "NotSelected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Submitted,
        Viewed,
        Shortlisted,
        NotSelected,
    };

    /// <summary>The only statuses an employer may set by hand.</summary>
    public static readonly IReadOnlySet<string> EmployerSettable = new HashSet<string>(StringComparer.Ordinal)
    {
        Shortlisted,
        NotSelected,
    };
}
