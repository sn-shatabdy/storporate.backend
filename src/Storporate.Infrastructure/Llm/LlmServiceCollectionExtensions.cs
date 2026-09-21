using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Infrastructure.Llm;

/// <summary>DI entry point wiring the Bionic-backed <see cref="ILlmClient"/> implementation.</summary>
public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers a named <see cref="HttpClient"/> pointed at <see cref="BionicOptions.BaseUrl"/>
    /// and <see cref="ILlmClient"/> as <see cref="BionicLlmProvider"/>. Assumes
    /// <see cref="BionicOptions"/> is already bound and validated (see the Options-pattern
    /// registration in <c>Program.cs</c>).
    /// </summary>
    public static IServiceCollection AddBionicLlmProvider(this IServiceCollection services)
    {
        services.AddHttpClient(LlmHttpClientNames.Bionic, (serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BionicOptions>>().Value;

            // BaseAddress must end in "/" for a relative request URI ("chat/completions") to
            // combine correctly instead of replacing the "/v1" path segment.
            httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

            // Generous timeout: a local reasoning model can legitimately take a while to finish
            // its internal chain-of-thought before returning, especially on first load.
            httpClient.Timeout = TimeSpan.FromSeconds(120);
        });

        services.AddTransient<ILlmClient, BionicLlmProvider>();

        return services;
    }

    /// <summary>
    /// Registers the embedding client and its named <see cref="HttpClient"/>. Assumes
    /// <see cref="BionicOptions"/> and <see cref="EmbeddingOptions"/> are already bound
    /// (see the Options-pattern registration in <c>Program.cs</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the chat and embeddings <see cref="HttpClient"/>s are split.</b>
    /// The common case is that both endpoints live on the same local Bionic server, so
    /// <see cref="EmbeddingOptions.BaseUrl"/> is empty and the embedding client just borrows
    /// <see cref="LlmHttpClientNames.Bionic"/>. When the embedding endpoint is hosted
    /// separately, the embedding client switches to
    /// <see cref="LlmHttpClientNames.BionicEmbedding"/> with the embedding-specific
    /// <see cref="EmbeddingOptions.BaseUrl"/>. Both names are registered unconditionally so
    /// the runtime never has to ask the DI container for a name it doesn't have.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBionicEmbeddings(this IServiceCollection services)
    {
        services.AddHttpClient(LlmHttpClientNames.BionicEmbedding, (serviceProvider, httpClient) =>
        {
            var bionicOptions = serviceProvider.GetRequiredService<IOptions<BionicOptions>>().Value;
            var embeddingOptions = serviceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;

            // When EmbeddingOptions.BaseUrl is empty the embedding client borrows the chat
            // client's HttpClient (Bionic), so this second client is only configured with a
            // real BaseAddress when a hosted embedding endpoint is in play.
            var baseUrl = string.IsNullOrWhiteSpace(embeddingOptions.BaseUrl)
                ? bionicOptions.BaseUrl
                : embeddingOptions.BaseUrl;

            httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

            // Embedding requests return quickly (no chain-of-thought); 60s matches the chat
            // timeout's outer envelope without being so generous that a hung server ties up a
            // background-worker slot indefinitely.
            httpClient.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<IEmbeddingClient, BionicEmbeddingClient>();

        return services;
    }
}
