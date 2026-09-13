namespace Storporate.Infrastructure.Storage;

/// <summary>
/// Wraps any failure talking to the artifact storage backend — a non-success S3-protocol
/// response, network/timeout failure, or an unreachable endpoint — into one clear exception
/// type. Callers never see a raw <see cref="Amazon.S3.AmazonS3Exception"/> or network exception;
/// they see this, which the API's <c>GlobalExceptionHandler</c> maps to a clean error response.
/// A missing key on <see cref="IArtifactStore.GetAsync"/> is deliberately NOT represented by this
/// exception — it is an expected outcome, surfaced as a <see langword="null"/> return instead.
/// </summary>
public sealed class ArtifactStorageException : Exception
{
    public ArtifactStorageException(string message)
        : base(message)
    {
    }

    public ArtifactStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
