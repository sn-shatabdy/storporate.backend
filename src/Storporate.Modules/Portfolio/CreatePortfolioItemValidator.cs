using FluentValidation;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Validates the <see cref="CreatePortfolioItemRequest"/> shape before any storage write
/// or DB insert runs. Enforces the four "real" invariants the plan calls out:
/// exactly one of (file, url) is present, file size &amp; content-type are within the
/// allowed envelope, the category is from the fixed set, and
/// <see cref="CreatePortfolioItemRequest.CustomCategoryText"/> is supplied when the student
/// picks <see cref="PortfolioCategories.Other"/>.
/// </summary>
/// <remarks>
/// <para>
/// Error codes follow the same
/// <c>&lt;field&gt;_&lt;reason&gt;</c> + human-readable message convention the
/// Identity validators use (<see cref="Identity.RequestOtpValidator"/>,
/// <see cref="Identity.VerifyOtpValidator"/>); the GlobalExceptionHandler reads
/// <c>ErrorCode</c> for the API surface response, so a typo here would silently
/// change the wire-level error code a client sees. Each rule carries an explicit
/// <c>.WithErrorCode(...)</c> for that reason.
/// </para>
/// <para>
/// <b>Why the file rules run only when the file is present.</b> Exactly-one-of is
/// the rule that decides which branch the handler takes; the per-file rules are
/// intentionally scoped to <c>When(request =&gt; request.File is not null, ...)</c>
/// predicates so a link-only submission doesn't trigger a "file_too_large" error
/// code on a request that never carried a file. Same logic in reverse for the
/// URL rule.
/// </para>
/// </remarks>
public sealed class CreatePortfolioItemValidator : AbstractValidator<CreatePortfolioItemRequest>
{
    /// <summary>100 MB hard cap, in bytes. Matches the plan's
    /// <c>"POST with a file over 100MB returns 400 before any storage write occurs"</c>
    /// acceptance criterion.</summary>
    public const long MaxFileSizeBytes = 100L * 1024L * 1024L;

    /// <summary>
    /// Backwards-compatible alias for <see cref="PortfolioContentTypes.Allowed"/>.
    /// Kept on the validator so existing call sites in the Portfolio module
    /// (and the unit tests for this validator) continue to compile; new code
    /// should prefer <see cref="PortfolioContentTypes.Allowed"/> directly so
    /// callers outside the Portfolio module do not have to depend on it.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes = PortfolioContentTypes.Allowed;

    public CreatePortfolioItemValidator()
    {
        RuleFor(request => request.Label)
            .NotEmpty()
            .WithErrorCode("label_required")
            .WithMessage("Label must not be empty.")
            .MaximumLength(200)
            .WithErrorCode("label_too_long")
            .WithMessage("Label must be at most 200 characters.");

        RuleFor(request => request.Category)
            .NotEmpty()
            .WithErrorCode("category_required")
            .WithMessage("Category must not be empty.")
            .Must(category => category is not null && PortfolioCategories.All.Contains(category))
            .WithErrorCode("category_unrecognized")
            .WithMessage("Category must be one of the allowed portfolio categories.");

        // Category == "Other" requires CustomCategoryText — the plan calls this out as a
        // separate acceptance criterion. Scoped via When() so non-Other categories never
        // see this rule at all.
        RuleFor(request => request.CustomCategoryText)
            .NotEmpty()
            .WithErrorCode("custom_category_text_required")
            .WithMessage("CustomCategoryText must not be empty when Category is 'Other'.")
            .When(request => request.Category == PortfolioCategories.Other);

        // Exactly-one-of (File, ExternalUrl) is enforced by a single custom predicate
        // that owns the "submission_source_conflict" error code. The handler dispatches
        // on whichever side is present, so any request that doesn't satisfy exactly-one-of
        // is rejected before either storage write or DB insert runs.
        RuleFor(request => request)
            .Must(HasExactlyOneSubmissionSource)
            .WithErrorCode("submission_source_conflict")
            .WithMessage("Provide exactly one of a file upload or an external URL, not both.");

        // Per-file rules — only run when File is present, so a link-only submission
        // never triggers a "file_too_large" / "file_content_type_not_allowed" error.
        RuleFor(request => request.File!.Length)
            .LessThanOrEqualTo(MaxFileSizeBytes)
            .WithErrorCode("file_too_large")
            .WithMessage($"File must be at most {MaxFileSizeBytes} bytes (100 MB).")
            .When(IsFileSubmission);

        RuleFor(request => request.File!.ContentType)
            .Must(contentType => contentType is not null && AllowedContentTypes.Contains(contentType))
            .WithErrorCode("file_content_type_not_allowed")
            .WithMessage("File content type is not in the allowed list.")
            .When(IsFileSubmission);

        // Per-URL rule — only runs on a link submission (no file, URL supplied).
        // Combined When() predicate (file absent AND url non-empty) means a "neither"
        // request surfaces the conflict error alone, not a parade of url-validity
        // errors on top of it.
        RuleFor(request => request.ExternalUrl!)
            .NotEmpty()
            .WithErrorCode("external_url_required")
            .WithMessage("ExternalUrl must not be empty.")
            .Must(BeAWellFormedAbsoluteHttpOrHttpsUri)
            .WithErrorCode("external_url_invalid")
            .WithMessage("ExternalUrl must be a well-formed absolute http or https URL.")
            .When(IsLinkSubmission);
    }

    private static bool IsFileSubmission(CreatePortfolioItemRequest request) =>
        request.File is not null;

    private static bool IsLinkSubmission(CreatePortfolioItemRequest request) =>
        request.File is null && !string.IsNullOrWhiteSpace(request.ExternalUrl);

    private static bool HasExactlyOneSubmissionSource(CreatePortfolioItemRequest request)
    {
        var hasFile = request.File is not null;
        var hasUrl = !string.IsNullOrWhiteSpace(request.ExternalUrl);
        return hasFile ^ hasUrl;
    }

    private static bool BeAWellFormedAbsoluteHttpOrHttpsUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }
}