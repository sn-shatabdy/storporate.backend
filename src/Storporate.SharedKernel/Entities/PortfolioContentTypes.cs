namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The full allowlist of content types for uploaded portfolio items. Mirrors
/// the plan's constraints/assumptions section: documents, images, video, archives.
/// Executables / scripts are deliberately omitted. Lives in <c>SharedKernel</c>
/// so modules that only need to <em>serve</em> an already-stored file (e.g. the
/// CandidateReview drill-down) can reference the same set without depending on the
/// Portfolio sibling module that owns the upload-time validation.
/// </summary>
/// <remarks>
/// Order-independent; comparisons are ordinal-case-insensitive. The portfolio
/// upload validator (<see cref="Storporate.Modules.Portfolio.CreatePortfolioItemValidator"/>)
/// is the authoritative source of truth — adding a new type requires only
/// appending it here.
/// </remarks>
public static class PortfolioContentTypes
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "text/plain",
        "text/csv",
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "video/mp4",
        "video/quicktime",
        "video/webm",
        "application/zip",
    };
}