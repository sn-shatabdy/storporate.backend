namespace Storporate.SharedKernel.Storage;

/// <summary>
/// Provider-agnostic entry point for blob/artifact storage (local MinIO today, real Cloudflare
/// R2 later — both are S3-compatible, see the plan's DEVIATION note on MinIO standing in for R2
/// until a payment method exists). Concrete implementations live in
/// <c>Storporate.Infrastructure</c>; callers in modules/API code depend only on this interface,
/// never on a specific storage SDK.
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Uploads <paramref name="content"/> under <paramref name="key"/>, overwriting any existing
    /// object at that key. Throws <c>ArtifactStorageException</c> (defined in
    /// <c>Storporate.Infrastructure</c>) if the upload fails for any reason.
    /// </summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the object at <paramref name="key"/>. Returns <see langword="null"/> when no
    /// object exists at that key — a missing key is an expected outcome, not a failure. Throws
    /// <c>ArtifactStorageException</c> for any other failure (unreachable storage, auth, etc.).
    /// </summary>
    Task<ArtifactContent?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the object at <paramref name="key"/>. Idempotent: deleting a key that does not
    /// exist is not an error (matches S3-compatible delete semantics). Throws
    /// <c>ArtifactStorageException</c> if the delete could not be carried out (e.g. storage
    /// unreachable).
    /// </summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// The content and declared content type of a downloaded artifact. The caller owns
/// <see cref="Content"/> and is responsible for disposing it.
/// </summary>
public sealed record ArtifactContent(Stream Content, string? ContentType);
