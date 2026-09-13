using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Storporate.SharedKernel.Storage;

namespace Storporate.Infrastructure.Storage;

/// <summary>
/// <see cref="IArtifactStore"/> backed by any S3-compatible endpoint — local MinIO today, real
/// Cloudflare R2 later (see the plan's DEVIATION note: MinIO stands in for R2 until a payment
/// method exists). The <see cref="IAmazonS3"/> client injected here already plays the role of
/// Docomate's <c>IBlobStorageClient</c> layer (it is itself an interface, registered and
/// configured once in DI — see <see cref="StorageServiceCollectionExtensions"/>), so this class
/// does not add a second hand-rolled wrapper interface around it; that would only forward every
/// call 1:1 and add no testability or seam that <see cref="IAmazonS3"/> doesn't already provide.
/// Every S3 SDK failure is wrapped into <see cref="ArtifactStorageException"/> so it never
/// escapes uncaught — it still propagates to the API's global exception handler rather than being
/// swallowed.
/// </summary>
public sealed class S3ArtifactStore : IArtifactStore
{
    private readonly IAmazonS3 _s3Client;
    private readonly ArtifactStorageOptions _options;
    private readonly ILogger<S3ArtifactStore> _logger;

    public S3ArtifactStore(
        IAmazonS3 s3Client,
        IOptions<ArtifactStorageOptions> options,
        ILogger<S3ArtifactStore> logger)
    {
        _s3Client = s3Client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task PutAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = _options.BucketName,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
            };

            await _s3Client.PutObjectAsync(request, cancellationToken);
        }
        catch (Exception ex) when (IsWrappableFailure(ex))
        {
            // Covers AmazonServiceException/AmazonS3Exception (a non-success response from the
            // storage endpoint), plain AWS SDK client-side connectivity failures, and a raw
            // HttpRequestException — observed in practice (e.g. "connection refused" against a
            // fully unreachable endpoint bypasses the SDK's own exception wrapping and surfaces
            // as HttpRequestException directly).
            _logger.LogError(ex, "Failed to upload artifact '{Key}' to bucket '{Bucket}'", key, _options.BucketName);
            throw new ArtifactStorageException($"Failed to upload artifact '{key}' to storage.", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's token was not cancelled — this is the SDK's own timeout firing.
            _logger.LogError("Request to artifact storage timed out while uploading '{Key}'", key);
            throw new ArtifactStorageException($"The request to artifact storage timed out while uploading '{key}'.");
        }
    }

    public async Task<ArtifactContent?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        GetObjectResponse response;
        try
        {
            response = await _s3Client.GetObjectAsync(_options.BucketName, key, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
        catch (Exception ex) when (IsWrappableFailure(ex))
        {
            _logger.LogError(ex, "Failed to download artifact '{Key}' from bucket '{Bucket}'", key, _options.BucketName);
            throw new ArtifactStorageException($"Failed to download artifact '{key}' from storage.", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Request to artifact storage timed out while downloading '{Key}'", key);
            throw new ArtifactStorageException($"The request to artifact storage timed out while downloading '{key}'.");
        }

        using (response)
        {
            var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return new ArtifactContent(buffer, response.Headers.ContentType);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            // S3-compatible DELETE is idempotent: deleting a key that does not exist still
            // returns success, so no NotFound special-casing is needed here.
            await _s3Client.DeleteObjectAsync(_options.BucketName, key, cancellationToken);
        }
        catch (Exception ex) when (IsWrappableFailure(ex))
        {
            _logger.LogError(ex, "Failed to delete artifact '{Key}' from bucket '{Bucket}'", key, _options.BucketName);
            throw new ArtifactStorageException($"Failed to delete artifact '{key}' from storage.", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Request to artifact storage timed out while deleting '{Key}'", key);
            throw new ArtifactStorageException($"The request to artifact storage timed out while deleting '{key}'.");
        }
    }

    private static bool IsNotFound(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound
        || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for any failure that should be wrapped into <see cref="ArtifactStorageException"/>
    /// rather than escaping raw: the AWS SDK's own exception hierarchy
    /// (<see cref="AmazonClientException"/>, covering both service-side and SDK client-side
    /// failures) plus a bare <see cref="HttpRequestException"/>, which a fully unreachable
    /// endpoint (e.g. connection refused) was confirmed to throw directly, bypassing the SDK's
    /// own wrapping.
    /// </summary>
    private static bool IsWrappableFailure(Exception ex) =>
        ex is AmazonClientException or HttpRequestException;
}
