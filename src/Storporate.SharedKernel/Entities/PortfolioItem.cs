namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A single item in a student's portfolio — either an uploaded file (PDF, image,
/// document, video, etc.) or a link to something hosted externally (portfolio, video, paper).
/// Captured by <c>POST /api/portfolio/items</c>, listed by
/// <c>GET /api/portfolio/items</c>, and hard-deleted (DB row + blob) by
/// <c>DELETE /api/portfolio/items/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IAccountScoped"/> — the account column is non-nullable, foreign-keyed
/// to <see cref="User.Id"/> with <see cref="Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict"/>
/// — so STOR-62's three-layer isolation pipeline (EF Core global query filter, save-time
/// interceptor, PostgreSQL row-level-security policy) is picked up automatically by
/// <c>WriteDbContext.OnModelCreating</c> with zero new isolation code. A DELETE for another
/// account's row simply returns no rows from the query and the handler treats that as a 404,
/// matching the existing tenant-isolation pattern.
/// </para>
/// <para>
/// <b>Storage model.</b> File-type submissions carry an <see cref="IArtifactStore"/> key plus
/// the original filename, content type, and byte size; link-type submissions carry only
/// <see cref="ExternalUrl"/>. The split keeps the database column set narrow: a row is either
/// "all about a file" or "all about a URL", never a confused mixture of both.
/// <see cref="SubmissionType"/> disambiguates which side a row is on at read time.
/// </para>
/// </remarks>
public sealed class PortfolioItem : IAccountScoped
{
    public Guid Id { get; set; }

    /// <summary>The owning <see cref="User.Id"/>. See class remarks.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Navigation to the owning <see cref="User"/>. Not required at insert time —
    /// EF Core populates it from <see cref="AccountId"/> when the principal is loaded.</summary>
    public User? Account { get; set; }

    /// <summary>The portfolio item's human-readable title/name as supplied by the student.</summary>
    public required string Label { get; set; }

    /// <summary>One of <see cref="PortfolioCategories"/>.</summary>
    public required string Category { get; set; }

    /// <summary>Populated only when <see cref="Category"/> equals <see cref="PortfolioCategories.Other"/>;
    /// carries the student's free-text category name.</summary>
    public string? CustomCategoryText { get; set; }

    /// <summary>One of <see cref="PortfolioSubmissionTypes"/>.</summary>
    public required string SubmissionType { get; set; }

    /// <summary>The key used with <c>IArtifactStore</c>. Populated only when
    /// <see cref="SubmissionType"/> equals <see cref="PortfolioSubmissionTypes.File"/>.</summary>
    public string? StorageKey { get; set; }

    /// <summary>The filename the student uploaded. Captured for display; never used as a
    /// storage key (the storage backend generates its own keys). Populated only when
    /// <see cref="SubmissionType"/> equals <see cref="PortfolioSubmissionTypes.File"/>.</summary>
    public string? OriginalFileName { get; set; }

    /// <summary>The MIME content type of the uploaded file. Populated only when
    /// <see cref="SubmissionType"/> equals <see cref="PortfolioSubmissionTypes.File"/>.</summary>
    public string? ContentType { get; set; }

    /// <summary>The size of the uploaded file in bytes. Populated only when
    /// <see cref="SubmissionType"/> equals <see cref="PortfolioSubmissionTypes.File"/>.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>The external URL the student supplied. Populated only when
    /// <see cref="SubmissionType"/> equals <see cref="PortfolioSubmissionTypes.Link"/>.</summary>
    public string? ExternalUrl { get; set; }

    /// <summary>Optional free-text notes from the student describing the portfolio item.</summary>
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>One of <see cref="PortfolioAnalysisStatuses"/>. Defaults to
    /// <see cref="PortfolioAnalysisStatuses.NotAnalyzed"/> on insert so a freshly-created
    /// row is in the documented initial state without any application-side write, and so the
    /// migration adding the column can back-fill existing rows in-place via a SQL DEFAULT.
    /// STOR-38 Phase 2's background worker is what flips this through the rest of the
    /// state machine (<c>Analyzing</c> → <c>Analyzed</c> / <c>Failed</c> /
    /// <c>Unsupported</c>); Phase 3's retry endpoint flips <c>Failed</c> back to
    /// <c>NotAnalyzed</c> on a user-initiated retry.</summary>
    public string AnalysisStatus { get; set; } = PortfolioAnalysisStatuses.NotAnalyzed;

    /// <summary>UTC timestamp of the most recent successful analysis run (i.e. the last time
    /// <see cref="AnalysisStatus"/> was flipped to <see cref="PortfolioAnalysisStatuses.Analyzed"/>).
    /// Null for items that have never been analyzed or are still in <c>NotAnalyzed</c> /
    /// <c>Analyzing</c> / <c>Failed</c> / <c>Unsupported</c>. The portfolio list page sorts
    /// by this descending (after <see cref="CreatedAt"/>) when present.</summary>
    public DateTimeOffset? LastAnalyzedAt { get; set; }

    /// <summary>STOR-44 Phase 1: the student's per-item opt-in to employer drill-down.
    /// When true, an employer who has surfaced this item in a talent search (Phase 2) is
    /// permitted to open the original file or follow the original link behind the claim.
    /// Defaults to <see langword="false"/>; the migration back-fills existing rows to the
    /// same default. The student toggles this flag via
    /// <c>PUT /api/portfolio/items/{id}/sharing</c>. The flag is independent of
    /// <see cref="Entities.StudentSearchProfile.IsSearchable"/> — a student who has not opted
    /// in to employer search can still set this on individual items without effect (no index
    /// entry exists yet to carry the descriptor), and an opted-in student can leave the
    /// flag off to keep this one item private while the rest are shared.</summary>
    public bool ShareOriginalWithEmployers { get; set; }
}
