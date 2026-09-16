using Microsoft.AspNetCore.Http;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// The multipart/form-data bound shape for <c>POST /api/portfolio/items</c>.
/// Each field is annotated <c>[FromForm]</c> so the minimal-API binder treats the whole
/// record as a single form body rather than guessing — see the
/// <c>minimal-api-file-upload</c> skill's "IFormFile + other form fields" rule. The file
/// itself is optional; the validator enforces that exactly one of <see cref="File"/> or
/// <see cref="ExternalUrl"/> is present.
/// </summary>
/// <remarks>
/// All string fields are non-nullable on the record but the validator handles the
/// "missing" case explicitly (form fields absent from the multipart body bind as
/// <see langword="null"/>, not empty). <see cref="File"/> binds to a single
/// <see cref="IFormFile"/>; multi-file portfolio submissions are intentionally not in
/// scope for STOR-37 (a future story can extend this shape to
/// <see cref="IFormFileCollection"/> without breaking the single-file happy path).
/// </remarks>
public sealed class CreatePortfolioItemRequest
{
    /// <summary>The portfolio item's title/name as supplied by the student.</summary>
    public string? Label { get; init; }

    /// <summary>One of <see cref="Storporate.SharedKernel.Entities.PortfolioCategories"/>.</summary>
    public string? Category { get; init; }

    /// <summary>Free-text category name, populated only when <see cref="Category"/> is
    /// <see cref="Storporate.SharedKernel.Entities.PortfolioCategories.Other"/>.</summary>
    public string? CustomCategoryText { get; init; }

    /// <summary>Optional free-text description of the portfolio item.</summary>
    public string? Description { get; init; }

    /// <summary>External URL pointing at the portfolio item. Mutually exclusive with
    /// <see cref="File"/>; the validator enforces "exactly one of these is present."</summary>
    public string? ExternalUrl { get; init; }

    /// <summary>The uploaded file. Mutually exclusive with <see cref="ExternalUrl"/>; the
    /// validator enforces "exactly one of these is present." Null when the submission is
    /// a link instead of an upload.</summary>
    public IFormFile? File { get; init; }
}