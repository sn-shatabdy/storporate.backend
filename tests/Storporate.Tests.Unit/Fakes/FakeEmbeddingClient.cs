using Storporate.SharedKernel.Abstractions;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// Test double for <see cref="IEmbeddingClient"/>. Like the
/// <see cref="FakeLlmClient"/>, it owns a queue of pre-built responses so the
/// processor test can drive deterministic embeddings without spinning up a real
/// Bionic / OpenAI-compatible endpoint. The fake also records every call so
/// the assertion can pin what the processor embedded on a given tick.
/// </summary>
/// <remarks>
/// The default canned response is a single deterministic 768-dimensional
/// unit vector with a 1.0 at slot 0 and 0.0 elsewhere — the same shape the
/// talent-index repository tests use as the "nearest" baseline. Tests that
/// need a different response shape push their own entries via
/// <see cref="EnqueueResponse"/>.
/// </remarks>
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    private readonly Queue<float[][]> _responses = new();
    private readonly List<EmbeddingCall> _calls = new();

    /// <summary>Every recorded <see cref="EmbedAsync"/> call, in invocation order.</summary>
    public IReadOnlyList<EmbeddingCall> Calls => _calls;

    /// <summary>Total number of <see cref="EmbedAsync"/> calls made so far.</summary>
    public int CallCount => _calls.Count;

    /// <summary>
    /// Push a pre-built response. <see cref="EmbedAsync"/> dequeue's one entry
    /// per call and returns it verbatim. A test that fails to enqueue enough
    /// responses will see the default fallback vector below.
    /// </summary>
    public void EnqueueResponse(float[][] response)
    {
        _responses.Enqueue(response);
    }

    /// <summary>
    /// Push a single-vector response for one input string. Convenience
    /// overload for the common case where the caller passes a single search
    /// text.
    /// </summary>
    public void EnqueueResponse(float[] vector)
    {
        _responses.Enqueue(new[] { vector });
    }

    /// <summary>
    /// Push an <see cref="System.Exception"/> to be thrown on the next call,
    /// matching the <see cref="FakeLlmClient.EnqueueException"/> pattern so
    /// the processor's retry path can be exercised by a uniform fake API.
    /// </summary>
    public void EnqueueException(System.Exception exception)
    {
        _exceptionQueue.Enqueue(exception);
    }

    private readonly Queue<System.Exception> _exceptionQueue = new();

    public async Task<float[][]> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        _calls.Add(new EmbeddingCall(inputs.ToArray(), cancellationToken));

        if (_exceptionQueue.Count > 0)
        {
            throw _exceptionQueue.Dequeue();
        }

        if (_responses.Count > 0)
        {
            return _responses.Dequeue();
        }

        // Default response: a single deterministic 768-dim unit vector for
        // every input. The first input gets the "real" shape; subsequent
        // inputs get the same shape — the refresh processor always passes
        // exactly one input, so the default path keeps the test focused.
        var fallback = new float[inputs.Count][];
        for (var i = 0; i < inputs.Count; i++)
        {
            fallback[i] = BuildDefaultVector();
        }
        return await Task.FromResult(fallback);
    }

    private static float[] BuildDefaultVector() =>
        Enumerable.Range(0, Storporate.SharedKernel.Entities.TalentIndexConstants.EmbeddingDimensions)
            .Select(i => i == 0 ? 1f : 0f)
            .ToArray();
}

/// <summary>
/// Single recorded call to <see cref="IEmbeddingClient.EmbedAsync"/>. Held by
/// value so each row is immutable once captured.
/// </summary>
/// <param name="Inputs">The input strings passed to the call (snapshot — the
/// caller may reuse the underlying list).</param>
/// <param name="CancellationToken">The cancellation token the caller passed
/// in (preserved for reference; not thread-safe across waits).</param>
public sealed record EmbeddingCall(
    IReadOnlyList<string> Inputs,
    CancellationToken CancellationToken = default);