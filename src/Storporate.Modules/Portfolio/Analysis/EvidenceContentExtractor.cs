using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Storage;
using UglyToad.PdfPig;

namespace Storporate.Modules.Portfolio.Analysis;

/// <summary>
/// The result of one <see cref="EvidenceContentExtractor.ExtractAsync"/> call. The
/// extractor returns either a positive <see cref="Text"/> payload (the analyzer may
/// call the LLM), or an <see cref="Outcome"/> of <see cref="ExtractionOutcome.Unsupported"/>
/// to tell the worker to short-circuit straight to
/// <see cref="PortfolioAnalysisStatuses.Unsupported"/> without ever touching
/// <c>ILlmClient</c>.
/// </summary>
/// <param name="Outcome">
/// <see cref="ExtractionOutcome.Text"/> when <see cref="Text"/> carries analyzable
/// content; <see cref="ExtractionOutcome.Unsupported"/> when the evidence type has
/// no text-extraction path (e.g. raw video, image, or design-tool binary).
/// </param>
/// <param name="Text">The extracted analyzable text. Always non-null when
/// <paramref name="Outcome"/> is <see cref="ExtractionOutcome.Text"/>; may be
/// <see langword="null"/> when <paramref name="Outcome"/> is
/// <see cref="ExtractionOutcome.Unsupported"/>.</param>
public sealed record EvidenceExtractionResult(ExtractionOutcome Outcome, string? Text)
{
    /// <summary>Successful extraction with <paramref name="Text"/> ready for the LLM.</summary>
    public static EvidenceExtractionResult WithText(string text) =>
        new(ExtractionOutcome.Text, text);

    /// <summary>Evidence has no extractable text path — short-circuit to
    /// <see cref="PortfolioAnalysisStatuses.Unsupported"/> without an LLM call.</summary>
    public static EvidenceExtractionResult Unsupported() =>
        new(ExtractionOutcome.Unsupported, null);
}

/// <summary>The two terminal outcomes the worker knows how to handle.</summary>
public enum ExtractionOutcome
{
    /// <summary>Text was successfully extracted — proceed to the LLM call.</summary>
    Text,

    /// <summary>The evidence type cannot be analyzed by this pipeline — record
    /// <see cref="PortfolioAnalysisStatuses.Unsupported"/> and stop.</summary>
    Unsupported,
}

/// <summary>
/// Resolves a <see cref="PortfolioItem"/> to the analyzable text the STOR-38 background
/// worker passes to <c>ILlmClient</c>. Branched on <see cref="PortfolioItem.SubmissionType"/>:
/// link submissions concatenate the student's own <see cref="PortfolioItem.Label"/>,
/// <see cref="PortfolioItem.Description"/>, and <see cref="PortfolioItem.Category"/>;
/// file submissions stream the bytes out of <see cref="IArtifactStore"/> and extract
/// text through a content-type-derived path (PDF via PdfPig, DOCX via OpenXML SDK,
/// plain text and source-code MIME types read as UTF-8); files whose
/// <see cref="PortfolioItem.Category"/> (or MIME type) maps to a non-text-extractable
/// binary — video, image, raw design-tool file — return
/// <see cref="EvidenceExtractionResult.Unsupported()"/> so the worker records
/// <see cref="PortfolioAnalysisStatuses.Unsupported"/> without ever calling the LLM.
/// </summary>
/// <remarks>
/// <para>
/// <b>No external URL fetch.</b> Link submissions are analyzed purely from the
/// student's own <c>Label</c> + <c>Description</c> + <c>Category</c> text per the
/// plan's hard constraint: this story does not fetch or scrape the URL the student
/// submitted. The tradeoff (weaker analysis quality for links vs. files) is
/// accepted — see the STOR-38 plan, Constraints, "link-type evidence is analyzed
/// from the student's own Label/Description/Category text only".
/// </para>
/// <para>
/// <b>Local-only extraction.</b> Both PdfPig and the OpenXML SDK operate in-process
/// against the bytes already in <see cref="IArtifactStore"/> — there is no new
/// external API dependency, matching the local-first architecture constraint.
/// </para>
/// <para>
/// <b>What "unsupported" means.</b> The worker treats this branch as terminal and
/// never retries — see the <see cref="PortfolioAnalysisStatuses.Unsupported"/>
/// doc-comment's "retrying can never succeed" rationale. The set is enumerated in
/// <see cref="IsUnsupportedCategory"/> and <see cref="IsUnsupportedContentType"/>;
/// anything not on those allow-lists that the worker still can't extract text from
/// (e.g. a future raw design format) gets <see cref="EvidenceExtractionResult.Unsupported"/>
/// as a safe default rather than a wasted LLM call.
/// </para>
/// </remarks>
public sealed class EvidenceContentExtractor
{
    private readonly IArtifactStore _artifactStore;
    private readonly ILogger<EvidenceContentExtractor> _logger;

    /// <summary>Content types the extractor treats as raw text (UTF-8 decoded).</summary>
    private static readonly IReadOnlySet<string> TextContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // The plan's enumerated allow-list for plain-text / source-code files.
        "text/plain",
        "text/csv",
        "text/markdown",
        "text/html",
        "text/xml",
        "application/json",
        "application/xml",
        "application/javascript",
        "application/x-javascript",
        "application/typescript",
        "application/x-typescript",
        "text/x-csharp",
        "text/x-c",
        "text/x-c++",
        "text/x-python",
        "text/x-java",
        "text/x-go",
        "text/x-rust",
        "text/x-shellscript",
        "text/x-sql",
    };

    /// <summary>Content types with a dedicated extractor implementation.</summary>
    private static readonly IReadOnlySet<string> PdfContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
    };

    private static readonly IReadOnlySet<string> DocxContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    public EvidenceContentExtractor(
        IArtifactStore artifactStore,
        ILogger<EvidenceContentExtractor> logger)
    {
        _artifactStore = artifactStore;
        _logger = logger;
    }

    public async Task<EvidenceExtractionResult> ExtractAsync(
        PortfolioItem portfolioItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(portfolioItem);

        if (portfolioItem.SubmissionType == PortfolioSubmissionTypes.Link)
        {
            return EvidenceExtractionResult.WithText(BuildLinkText(portfolioItem));
        }

        // SubmissionType == File from here on. File submissions always carry a
        // StorageKey (CreatePortfolioItemValidator enforces the file-or-link
        // exactly-one rule, so a Link submission never has a StorageKey). Defensive
        // null-guard rather than a hard throw — if a row is somehow mis-shaped,
        // short-circuit to Unsupported so the worker doesn't loop on broken data.
        if (string.IsNullOrEmpty(portfolioItem.StorageKey))
        {
            _logger.LogWarning(
                "Portfolio item {PortfolioItemId} is type File but has no StorageKey — short-circuiting to Unsupported.",
                portfolioItem.Id);
            return EvidenceExtractionResult.Unsupported();
        }

        // Category-based fast path: even if a future ContentType is added on top of
        // the existing allow-list, a Video / DesignFile / Certificate (raw image)
        // submission is by definition non-text-extractable and the category tells us
        // so before we even ask IArtifactStore for the bytes.
        if (IsUnsupportedCategory(portfolioItem.Category))
        {
            return EvidenceExtractionResult.Unsupported();
        }

        var contentType = portfolioItem.ContentType;
        if (string.IsNullOrEmpty(contentType) || IsUnsupportedContentType(contentType))
        {
            return EvidenceExtractionResult.Unsupported();
        }

        var artifact = await _artifactStore
            .GetAsync(portfolioItem.StorageKey, cancellationToken)
            .ConfigureAwait(false);

        // A missing blob is unexpected (the delete pipeline is the only legitimate
        // path to remove the artifact and it also removes the row), but we don't
        // want a single missing blob to spin the worker — treat it as Unsupported.
        if (artifact is null)
        {
            _logger.LogWarning(
                "Artifact {StorageKey} for portfolio item {PortfolioItemId} was not found in storage — short-circuiting to Unsupported.",
                portfolioItem.StorageKey, portfolioItem.Id);
            return EvidenceExtractionResult.Unsupported();
        }

        await using (artifact.Content)
        {
            if (PdfContentTypes.Contains(contentType))
            {
                return EvidenceExtractionResult.WithText(await ExtractPdfAsync(artifact.Content, cancellationToken).ConfigureAwait(false));
            }

            if (DocxContentTypes.Contains(contentType))
            {
                return EvidenceExtractionResult.WithText(await ExtractDocxAsync(artifact.Content, cancellationToken).ConfigureAwait(false));
            }

            if (TextContentTypes.Contains(contentType))
            {
                return EvidenceExtractionResult.WithText(await ExtractPlainTextAsync(artifact.Content, cancellationToken).ConfigureAwait(false));
            }
        }

        // Unrecognized content type that didn't trip the unsupported-category or
        // unsupported-content-type fast paths above — safe default is Unsupported
        // (better than silently feeding binary garbage into the LLM).
        return EvidenceExtractionResult.Unsupported();
    }

    /// <summary>
    /// Builds the analyzable text for a link-type item. Order is intentional: Label
    /// first (the most identifying signal), then Description (the student's own
    /// free-text explanation), then Category (a single-token context anchor). The
    /// three are joined by double newlines so the model can see them as
    /// separately-attributed fields rather than a run-on sentence.
    /// </summary>
    private static string BuildLinkText(PortfolioItem item)
    {
        var parts = new List<string>(capacity: 3);
        if (!string.IsNullOrWhiteSpace(item.Label))
        {
            parts.Add($"Label: {item.Label}");
        }

        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            parts.Add($"Description: {item.Description}");
        }

        parts.Add($"Category: {item.Category}");

        // If the student left Label/Description both empty the model still gets at
        // least the Category so it has some anchor to reason about.
        return string.Join("\n\n", parts);
    }

    private static async Task<string> ExtractPlainTextAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ExtractPdfAsync(Stream stream, CancellationToken cancellationToken)
    {
        // PdfPig's PdfDocument is IDisposable and reads lazily; iterating the pages
        // here forces the parse so a corrupt document surfaces its exception now
        // rather than on first ToString() of a page.
        return await Task.Run(() =>
        {
            using var document = PdfDocument.Open(stream);
            var builder = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.AppendLine(page.Text);
                builder.AppendLine();
            }
            return builder.ToString();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ExtractDocxAsync(Stream stream, CancellationToken cancellationToken)
    {
        // OpenXml's WordprocessingDocument.Open(stream, isEditable: false) is the
        // read-only-mode entry point — leaves the underlying stream open and the
        // caller disposes the document, which is exactly the lifetime we want here.
        return await Task.Run(() =>
        {
            using var document = WordprocessingDocument.Open(stream, isEditable: false);
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new InvalidOperationException("DOCX file has no MainDocumentPart/Document/Body.");

            var builder = new StringBuilder();
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.AppendLine(paragraph.InnerText);
            }
            return builder.ToString();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Categories the worker can never analyze from text extraction. See
    /// <see cref="PortfolioCategories"/> for the full set; Video and DesignFile are
    /// the canonical examples (raw video frames, raw design-tool binary).</summary>
    private static bool IsUnsupportedCategory(string category) =>
        string.Equals(category, PortfolioCategories.Video, StringComparison.Ordinal)
        || string.Equals(category, PortfolioCategories.DesignFile, StringComparison.Ordinal);

    /// <summary>Content types the worker can never extract text from. Image MIME
    /// types are the canonical case (no OCR pipeline exists in this story).</summary>
    private static bool IsUnsupportedContentType(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(contentType, "application/zip", StringComparison.OrdinalIgnoreCase)
        || string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
}
