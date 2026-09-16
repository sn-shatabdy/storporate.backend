using Storporate.SharedKernel.Storage;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// Test double for <see cref="IArtifactStore"/>. Records every <see cref="PutAsync"/> and
/// <see cref="DeleteAsync"/> call against in-memory dictionaries so handler tests can assert
/// exactly which storage keys were written or removed for a given run, without going through
/// the real S3-compatible backend (which is unreachable from the unit test suite's
/// <c>WebApplicationFactory</c>/InMemory configurations).
/// </summary>
/// <remarks>
/// Hand-written (no mocking framework) to match the rest of this suite's style
/// (<see cref="FakeAuditLogWriter"/>, <see cref="FakeGoogleIdTokenValidator"/>,
/// <see cref="FakeJwtTokenService"/>): small, deterministic, with a public
/// read-only view of recorded operations.
/// </remarks>
public sealed class FakeArtifactStore : IArtifactStore
{
    private readonly Dictionary<string, byte[]> _objects = [];

    /// <summary>Every <see cref="PutAsync"/> call, in invocation order.</summary>
    public IReadOnlyList<PutCall> PutCalls => _putCalls;

    /// <summary>Every <see cref="DeleteAsync"/> call, in invocation order.</summary>
    public IReadOnlyList<DeleteCall> DeleteCalls => _deleteCalls;

    private readonly List<PutCall> _putCalls = [];
    private readonly List<DeleteCall> _deleteCalls = [];

    /// <summary>All keys currently "stored" — useful for asserting a key was added or removed.</summary>
    public IReadOnlyCollection<string> StoredKeys => _objects.Keys;

    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        _objects[key] = buffer.ToArray();
        _putCalls.Add(new PutCall(key, contentType, buffer.ToArray().LongLength));
        return Task.CompletedTask;
    }

    public Task<ArtifactContent?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_objects.TryGetValue(key, out var bytes))
        {
            return Task.FromResult<ArtifactContent?>(null);
        }

        var stream = new MemoryStream(bytes);
        return Task.FromResult<ArtifactContent?>(new ArtifactContent(stream, "application/octet-stream"));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _objects.Remove(key);
        _deleteCalls.Add(new DeleteCall(key));
        return Task.CompletedTask;
    }

    /// <summary>One recorded call to <see cref="IArtifactStore.PutAsync"/>.</summary>
    public sealed record PutCall(string Key, string ContentType, long ByteCount);

    /// <summary>One recorded call to <see cref="IArtifactStore.DeleteAsync"/>.</summary>
    public sealed record DeleteCall(string Key);
}