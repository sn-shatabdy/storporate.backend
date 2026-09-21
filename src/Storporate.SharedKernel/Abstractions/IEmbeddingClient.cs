namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// Provider-agnostic entry point for vector embeddings against whichever model
/// backs the platform's talent-search index (the local Bionic server today,
/// the same OpenAI-compatible <c>/embeddings</c> surface a hosted provider
/// would expose later). Concrete providers live in
/// <c>Storporate.Infrastructure</c>; callers in modules/API code depend only
/// on this interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why batched.</b> The single embedding call the talent-search index
/// refresh needs is one string per student, so batching is rarely useful in
/// the hot path — but the interface accepts a list so the provider can chunk
/// large jobs and so the InMemory test fake can record every call's full
/// input shape in one <c>Call</c> record.
/// </para>
/// <para>
/// <b>Order-preserving.</b> The returned vectors MUST come back in the same
/// order as the inputs — providers that re-order rows server-side (the
/// OpenAI-compatible <c>data[].index</c> field exists for exactly this) have
/// to re-sort their response on the way out; an out-of-order result is
/// surfaced as <see cref="Storporate.Infrastructure.Llm.LlmProviderException"/>
/// so the job retry path can run, mirroring how a chat-completion response
/// error is handled.
/// </para>
/// </remarks>
public interface IEmbeddingClient
{
    /// <summary>
    /// Embed each input string as a fixed-dimensional float vector. The
    /// returned array's length equals <c>inputs.Count</c> and the order matches
    /// <paramref name="inputs"/> exactly. A count mismatch or a wrong
    /// dimension is surfaced as
    /// <see cref="Storporate.Infrastructure.Llm.LlmProviderException"/> so the
    /// caller (the talent-index refresh processor) can retry or fail the
    /// containing job.
    /// </summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default);
}
