namespace Storporate.Infrastructure.Llm;

/// <summary>Names of the <see cref="IHttpClientFactory"/>-managed clients used by LLM providers.</summary>
internal static class LlmHttpClientNames
{
    public const string Bionic = "Bionic";

    /// <summary>Name of the <see cref="IHttpClientFactory"/>-managed client
    /// used by the embedding provider when <see cref="EmbeddingOptions.BaseUrl"/>
    /// is empty (the common case — the chat and embeddings endpoints share the
    /// same local Bionic server). When <see cref="EmbeddingOptions.BaseUrl"/> is
    /// set, the provider registers / looks up a second named client so a
    /// future hosted embedding endpoint can have its own base URL without
    /// colliding with the chat endpoint.</summary>
    public const string BionicEmbedding = "BionicEmbedding";
}
