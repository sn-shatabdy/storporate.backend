using Storporate.SharedKernel.Storage;

namespace Storporate.Tests.Unit.DiscoveryHiring.Fakes;

/// <summary>
/// Test double for <see cref="IArtifactStore"/> that the STOR-44 Phase 2
/// drill-down tests use. Unlike <c>FakeArtifactStore</c> this variant
/// preserves the stored content type on the way out — Phase 2 needs the
/// <see cref="IArtifactStore.GetAsync"/> response's <see cref="ArtifactContent.ContentType"/>
/// to drive the streaming <c>Content-Type</c> header (the prompt's
/// "the STORED content type from the descriptor" rule is the
/// phase-2-specific one; other test sites always treat the type as
/// <c>application/octet-stream</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate test double, not a change to FakeArtifactStore.</b>
/// The existing <see cref="Fakes.FakeArtifactStore"/> is used by every
/// Portfolio / DiscoveryHiring / identity test that just needs to know
/// whether a key was put or deleted. Changing its <c>GetAsync</c> to
/// surface the stored content type would silently change the type the
/// Portfolio response handlers see (e.g. the EvidenceContentExtractor's
/// in-process pipeline), and a regression caught months later would have
/// no obvious cause. Phase 2's content-type sensitive tests get their
/// own double whose behaviour is documented at the call site that uses
/// it.
/// </para>
/// <para>
/// <b>Seeding.</b> The double carries two dictionaries: an explicit
/// per-key mapping (<see cref="Seed(string, Stream, string)"/>) for
/// tests that want a specific content type, and a default-content-type
/// fallback used when <see cref="SeedDefault(string, byte[])"/> / etc.
/// are called without a content type. The default seed flows through
/// <see cref="ResolveContentType"/> to match the production store's
/// "octet-stream fallback" rule so a test that stores an unknown type
/// still surfaces the same wire behavior the endpoint produces.
/// </para>
/// </remarks>
public sealed class SeededArtifactStore : IArtifactStore
{
    /// <summary>Per-key (Stream, ContentType) pairs registered via
    /// <see cref="Seed"/>. The first dictionary's value is the bytes
    /// to surface; the second is the stored content type.</summary>
    private readonly Dictionary<string, (Stream Content, string ContentType)> _objects = new(StringComparer.Ordinal);

    /// <summary>The default content type for keys registered via
    /// <see cref="SeedDefault(string, byte[], string?)"/>. Defaults
    /// to <c>application/octet-stream</c> — matches the production
    /// store's "unknown type" surface.</summary>
    public string DefaultContentType { get; set; } = "application/octet-stream";

    /// <summary>The total bytes returned by every <see cref="GetAsync"/>
    /// call so far. Diagnostic — used by the streaming test to assert
    /// the handler did not double-buffer.</summary>
    public long TotalBytesReturned { get; private set; }

    /// <summary>Every <see cref="GetAsync(string, CancellationToken)"/>
    /// call in invocation order — keys + whether the lookup resolved.
    /// Tests assert which storage keys the drill-down handler actually
    /// reached.</summary>
    public IReadOnlyList<GetCall> GetCalls => _getCalls;

    private readonly List<GetCall> _getCalls = new();

    public sealed record GetCall(string Key, bool Resolved);

    /// <summary>Register <paramref name="content"/> at <paramref name="key"/>
    /// under <paramref name="contentType"/>.</summary>
    public void Seed(string key, Stream content, string contentType)
    {
        ArgumentNullException.ThrowIfNull(content);
        _objects[key] = (content, contentType);
    }

    /// <summary>Register <paramref name="bytes"/> at <paramref name="key"/>
    /// under <paramref name="contentType"/> (or
    /// <see cref="DefaultContentType"/> when null).</summary>
    public void SeedDefault(string key, byte[] bytes, string? contentType = null) =>
        Seed(key, new MemoryStream(bytes), contentType ?? DefaultContentType);

    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        // Tests that need a Write path can call Seed directly; PutAsync
        // exists only because IArtifactStore requires it. Forward the
        // stream into our dict so any Put-then-Get sequence works.
        ArgumentNullException.ThrowIfNull(content);
        var buffer = new MemoryStream();
        content.CopyTo(buffer);
        buffer.Position = 0;
        _objects[key] = (buffer, contentType);
        return Task.CompletedTask;
    }

    public Task<ArtifactContent?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_objects.TryGetValue(key, out var stored))
        {
            _getCalls.Add(new GetCall(key, Resolved: false));
            return Task.FromResult<ArtifactContent?>(null);
        }

        TotalBytesReturned += stored.Content.Length;
        _getCalls.Add(new GetCall(key, Resolved: true));
        return Task.FromResult<ArtifactContent?>(new ArtifactContent(stored.Content, stored.ContentType));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _objects.Remove(key);
        return Task.CompletedTask;
    }
}
