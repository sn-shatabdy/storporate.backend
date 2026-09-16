using Microsoft.AspNetCore.Http;
using Storporate.Modules.Portfolio;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// TDD coverage for <see cref="CreatePortfolioItemValidator"/>. Each test pins one rule
/// (file/url conflict, file size, content type, category, "Other" custom text, URL
/// shape) by name + error code so a future change to the rule list can't silently
/// regress one of the plan's acceptance criteria without breaking the test by name.
/// </summary>
public class CreatePortfolioItemValidatorTests
{
    private readonly CreatePortfolioItemValidator _validator = new();

    [Fact]
    public void Valid_FileSubmission_Passes()
    {
        var request = CreateRequest(file: CreateFormFile("hello world", "doc.pdf", "application/pdf"));

        var result = _validator.Validate(request);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorCode)));
    }

    [Fact]
    public void Valid_LinkSubmission_Passes()
    {
        var request = CreateRequest(
            file: null,
            externalUrl: "https://example.com/portfolio");

        var result = _validator.Validate(request);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorCode)));
    }

    [Fact]
    public void Both_File_And_Url_FailsWithSubmissionSourceConflict()
    {
        var request = CreateRequest(
            file: CreateFormFile("hello", "doc.pdf", "application/pdf"),
            externalUrl: "https://example.com/portfolio");

        var result = _validator.Validate(request);

        var failure = Assert.Single(result.Errors);
        Assert.Equal("submission_source_conflict", failure.ErrorCode);
    }

    [Fact]
    public void Neither_File_Nor_Url_FailsWithSubmissionSourceConflict()
    {
        var request = CreateRequest(file: null, externalUrl: null);

        var result = _validator.Validate(request);

        var failure = Assert.Single(result.Errors);
        Assert.Equal("submission_source_conflict", failure.ErrorCode);
    }

    [Fact]
    public void Oversized_File_FailsWithFileTooLarge()
    {
        // Construct a file whose Length reports oversize without actually
        // allocating 100 MB of memory: TestFormFile's Length override is what
        // the validator reads, not its backing byte count.
        var file = CreateFormFileWithOverriddenLength(
            length: CreatePortfolioItemValidator.MaxFileSizeBytes + 1,
            contentType: "application/pdf");

        var request = CreateRequest(file: file);

        var result = _validator.Validate(request);

        var failure = Assert.Single(result.Errors);
        Assert.Equal("file_too_large", failure.ErrorCode);
    }

    [Fact]
    public void Disallowed_ContentType_FailsWithFileContentTypeNotAllowed()
    {
        var file = CreateFormFile("echo", "malware.exe", "application/x-msdownload");

        var request = CreateRequest(file: file);

        var result = _validator.Validate(request);

        var failure = Assert.Single(result.Errors);
        Assert.Equal("file_content_type_not_allowed", failure.ErrorCode);
    }

    [Fact]
    public void Category_Other_Without_CustomCategoryText_FailsWithCustomCategoryTextRequired()
    {
        var request = CreateRequest(
            category: PortfolioCategories.Other,
            customCategoryText: null,
            file: null,
            externalUrl: "https://example.com/other");

        var result = _validator.Validate(request);

        var failure = Assert.Single(result.Errors);
        Assert.Equal("custom_category_text_required", failure.ErrorCode);
    }

    [Fact]
    public void Unrecognized_Category_FailsWithCategoryUnrecognized()
    {
        var request = CreateRequest(
            category: "NotARealCategory",
            file: null,
            externalUrl: "https://example.com/x");

        var result = _validator.Validate(request);

        var failure = Assert.Single(result.Errors);
        Assert.Equal("category_unrecognized", failure.ErrorCode);
    }

    [Fact]
    public void Empty_Label_FailsWithLabelRequired()
    {
        var request = CreateRequest(file: CreateFormFile("x", "doc.pdf", "application/pdf"));
        request = new CreatePortfolioItemRequest
        {
            Label = "",
            Category = request.Category,
            CustomCategoryText = request.CustomCategoryText,
            Description = request.Description,
            ExternalUrl = request.ExternalUrl,
            File = request.File,
        };

        var result = _validator.Validate(request);

        var failure = Assert.Single(result.Errors);
        Assert.Equal("label_required", failure.ErrorCode);
    }

    [Fact]
    public void Malformed_ExternalUrl_FailsWithExternalUrlInvalid()
    {
        var request = CreateRequest(
            file: null,
            externalUrl: "not-a-valid-url");

        var result = _validator.Validate(request);

        var failure = Assert.Single(result.Errors);
        Assert.Equal("external_url_invalid", failure.ErrorCode);
    }

    [Fact]
    public void NonHttp_ExternalUrl_Scheme_FailsWithExternalUrlInvalid()
    {
        var request = CreateRequest(
            file: null,
            externalUrl: "ftp://example.com/file");

        var result = _validator.Validate(request);

        var failure = Assert.Single(result.Errors);
        Assert.Equal("external_url_invalid", failure.ErrorCode);
    }

    private static CreatePortfolioItemRequest CreateRequest(
        IFormFile? file = null,
        string? externalUrl = null,
        string? category = null,
        string? customCategoryText = null)
    {
        return new CreatePortfolioItemRequest
        {
            Label = "My Portfolio",
            Category = category ?? PortfolioCategories.PortfolioLink,
            CustomCategoryText = customCategoryText,
            Description = "Optional description",
            ExternalUrl = externalUrl,
            File = file,
        };
    }

    private static IFormFile CreateFormFile(string content, string fileName, string contentType)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new TestFormFile
        {
            FileName = fileName,
            ContentType = contentType,
            Length = bytes.LongLength,
        };
    }

    /// <summary>
    /// Builds a <see cref="TestFormFile"/> whose <see cref="Length"/> can be set
    /// independently of its backing byte count, for the oversized-file validator
    /// test (so the test does not need to allocate 100 MB of memory just to
    /// exercise the size check).
    /// </summary>
    private static IFormFile CreateFormFileWithOverriddenLength(long length, string contentType)
    {
        return new TestFormFile
        {
            FileName = "x.bin",
            ContentType = contentType,
            Length = length,
        };
    }
}

/// <summary>
/// Lightweight <see cref="IFormFile"/> stand-in for unit tests. Only
/// <c>FileName</c>, <c>ContentType</c>, and <c>Length</c> are exercised by the
/// validator — there is no need to spin up a real multipart binder here. The
/// backing bytes are derived from <see cref="Length"/> so the handler test's
/// artifact-store assertions (byte count == what the handler actually streamed)
/// match the test file's reported <see cref="Length"/>.
/// </summary>
internal sealed class TestFormFile : IFormFile
{
    public string ContentType { get; init; } = string.Empty;

    public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{FileName}\"";

    public IHeaderDictionary Headers => new HeaderDictionary();

    public long Length { get; init; }

    public string Name => "file";

    public string FileName { get; init; } = string.Empty;

    private byte[] PayloadBytes() => new byte[Length];

    public Stream OpenReadStream() => new MemoryStream(PayloadBytes());

    public void CopyTo(Stream target) => target.Write(PayloadBytes(), 0, (int)Length);

    public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
        target.WriteAsync(PayloadBytes(), 0, (int)Length, cancellationToken);
}