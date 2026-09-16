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
}
