using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Infrastructure.Llm;

/// <summary>
/// <see cref="IEmbeddingClient"/> backed by the same locally-running Bionic
/// server the chat provider uses, served on an OpenAI-compatible
/// <c>/embeddings</c> endpoint. POSTs one batch per call (chunked to
/// <see cref="EmbeddingOptions.MaxBatchSize"/>), reorders the response rows
/// by the OpenAI <c>data[].index</c> field, and verifies each row's vector
/// dimension against <see cref="EmbeddingOptions.Dimensions"/> before
/// returning.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sibling <see cref="HttpLlmClientBase{TResponse}"/>-style error
/// wrapper.</b> The provider doesn't extend <see cref="HttpLlmClientBase{TResponse}"/>
/// directly — that base is hard-wired to <see cref="ILlmClient"/>. Instead it
/// mirrors the same send / status-check / deserialize / wrap-as
/// <see cref="LlmProviderException"/> flow inline, so every failure mode the
/// chat provider can throw (timeout, network error, non-success status, empty
/// body, JSON deserialization failure) surfaces as the same
/// <see cref="LlmProviderException"/> and routes through
/// <c>GlobalExceptionHandler</c> / the job-retry path identically.
/// </para>
/// <para>
/// <b>Order-preservation.</b> A provider that returns <c>data[]</c> out of
/// input order is reordered by the <c>index</c> field; a provider that
/// omits <c>index</c>, or that returns fewer / more rows than the inputs
/// supplied, throws <see cref="LlmProviderException"/> with a precise message.
/// </para>
/// <para>
/// <b>Per-row dimension check.</b> The OpenAI-compatible surface is allowed
/// to vary dimension per response, so the provider verifies every returned
/// vector's length against <see cref="EmbeddingOptions.Dimensions"/> and
/// throws on the first mismatch. A mismatch is the strongest possible signal
/// that the configured model is wrong (or has been swapped without updating
/// the section) — surfacing it as a retryable <see cref="LlmProviderException"/>
/// makes the failure visible without silently storing a wrong-dimension
/// vector in Postgres.
/// </para>
/// </remarks>
public sealed class BionicEmbeddingClient : IEmbeddingClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EmbeddingOptions _options;
    private readonly BionicOptions _bionicOptions;
    private readonly ILogger<BionicEmbeddingClient> _logger;

    public BionicEmbeddingClient(
        IHttpClientFactory httpClientFactory,
        IOptions<EmbeddingOptions> options,
        IOptions<BionicOptions> bionicOptions,
        ILogger<BionicEmbeddingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _bionicOptions = bionicOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<float[][]> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var results = new float[inputs.Count][];

        var httpClientName = string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? LlmHttpClientNames.Bionic
            : LlmHttpClientNames.BionicEmbedding;
        var httpClient = _httpClientFactory.CreateClient(httpClientName);

        for (var batchStart = 0; batchStart < inputs.Count; batchStart += _options.MaxBatchSize)
        {
            var batchEnd = Math.Min(batchStart + _options.MaxBatchSize, inputs.Count);
            var batch = inputs
                .Skip(batchStart)
                .Take(batchEnd - batchStart)
                .ToArray();

            var batchVectors = await SendBatchAsync(httpClient, batch, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < batchVectors.Length; i++)
            {
                results[batchStart + i] = batchVectors[i];
            }
        }

        return results;
    }

    private async Task<float[][]> SendBatchAsync(
        HttpClient httpClient,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        var payload = new BionicEmbeddingsRequestPayload(_options.ModelId, inputs);

        using var request = new HttpRequestMessage(HttpMethod.Post, "embeddings")
        {
            Content = JsonContent.Create(payload),
        };

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach embedding provider at {BaseAddress}", httpClient.BaseAddress);
            throw new LlmProviderException(
                $"Failed to reach the embedding provider at '{httpClient.BaseAddress}'. Is it running?", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Request to embedding provider at {BaseAddress} timed out", httpClient.BaseAddress);
            throw new LlmProviderException(
                $"The request to the embedding provider at '{httpClient.BaseAddress}' timed out.");
        }

        using (httpResponse)
        {
            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                // Truncate before logging/throwing so a hosted provider that
                // echoes the input prompt in its 4xx body can't leak the
                // caller's raw query through the log/audit chain.
                var sanitizedBody = errorBody.Length > 200 ? errorBody[..200] + "…" : errorBody;
                _logger.LogError(
                    "Embedding provider returned {StatusCode} {ReasonPhrase}: {Body}",
                    (int)httpResponse.StatusCode,
                    httpResponse.ReasonPhrase,
                    sanitizedBody);
                throw new LlmProviderException(
                    $"Embedding provider returned {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}.");
            }

            BionicEmbeddingsResponse? response;
            try
            {
                response = await httpResponse.Content
                    .ReadFromJsonAsync<BionicEmbeddingsResponse>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new LlmProviderException("Failed to deserialize the embedding provider's response.", ex);
            }

            if (response is null)
            {
                throw new LlmProviderException("Embedding provider returned an empty response body.");
            }

            return MapResponse(response, inputs.Count);
        }
    }

    /// <summary>
    /// Reorder the response rows by their <c>index</c> field, verify the
    /// returned row count matches the input count, and verify every row's
    /// vector length matches <see cref="EmbeddingOptions.Dimensions"/>.
    /// </summary>
    private float[][] MapResponse(BionicEmbeddingsResponse response, int expectedCount)
    {
        if (response.Data is null || response.Data.Count == 0)
        {
            throw new LlmProviderException("Embedding provider returned a response with no data rows.");
        }

        if (response.Data.Count != expectedCount)
        {
            throw new LlmProviderException(
                $"Embedding provider returned {response.Data.Count} row(s); expected {expectedCount}.");
        }

        var byIndex = new BionicEmbeddingsResponseRow[expectedCount];
        var seenIndices = new bool[expectedCount];

        foreach (var row in response.Data)
        {
            if (row.Index < 0 || row.Index >= expectedCount)
            {
                throw new LlmProviderException(
                    $"Embedding provider returned row with index {row.Index}; expected range [0, {expectedCount}).");
            }

            if (seenIndices[row.Index])
            {
                throw new LlmProviderException(
                    $"Embedding provider returned a duplicate row with index {row.Index}.");
            }

            seenIndices[row.Index] = true;
            byIndex[row.Index] = row;
        }

        for (var i = 0; i < expectedCount; i++)
        {
            if (!seenIndices[i])
            {
                throw new LlmProviderException(
                    $"Embedding provider response was missing row with index {i}.");
            }
        }

        var vectors = new float[expectedCount][];
        for (var i = 0; i < expectedCount; i++)
        {
            var row = byIndex[i];
            if (row.Embedding is null)
            {
                throw new LlmProviderException(
                    $"Embedding provider row with index {i} contained a null vector.");
            }

            if (row.Embedding.Count != _options.Dimensions)
            {
                throw new LlmProviderException(
                    $"Embedding provider row with index {i} returned a vector of length " +
                    $"{row.Embedding.Count}; expected {_options.Dimensions}.");
            }

            vectors[i] = row.Embedding.ToArray();
        }

        return vectors;
    }
}

/// <summary>OpenAI-compatible <c>/embeddings</c> request body.</summary>
public sealed record BionicEmbeddingsRequestPayload(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

/// <summary>OpenAI-compatible <c>/embeddings</c> response body.</summary>
public sealed record BionicEmbeddingsResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<BionicEmbeddingsResponseRow>? Data);

/// <summary>One row of an OpenAI-compatible <c>/embeddings</c> response.
/// <see cref="Index"/> is the input position the row corresponds to; the
/// <see cref="BionicEmbeddingClient"/> sorts rows by it on the way out so the
/// caller's <c>results[i]</c> matches <c>inputs[i]</c>.</summary>
public sealed record BionicEmbeddingsResponseRow(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("embedding")] IReadOnlyList<float>? Embedding);
