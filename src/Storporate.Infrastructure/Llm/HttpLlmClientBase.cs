using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Infrastructure.Llm;

/// <summary>
/// Shared HTTP/auth/error-wrapping flow for every LLM provider: build the request, call the
/// named <see cref="IHttpClientFactory"/> client, check the status code, deserialize the raw
/// provider-shaped <typeparamref name="TResponse"/>, then hand it to <see cref="MapResponse"/>.
/// Concrete providers only implement the payload shape and the response mapping — everything
/// that can go wrong on the wire is wrapped here into a single <see cref="LlmProviderException"/>
/// so it never escapes as a raw network/deserialization exception, while still propagating up to
/// be caught by the API's global exception handler rather than being swallowed.
/// </summary>
public abstract class HttpLlmClientBase<TResponse> : ILlmClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    protected HttpLlmClientBase(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>The name this provider's <c>HttpClient</c> was registered under via <c>AddHttpClient</c>.</summary>
    protected abstract string HttpClientName { get; }

    /// <summary>Builds the provider-specific request (payload + relative URI) for a completion.</summary>
    protected abstract HttpRequestMessage BuildRequest(LlmCompletionRequest request);

    /// <summary>Maps the provider's raw, successfully-deserialized response into the shared result shape.</summary>
    protected abstract LlmCompletionResult MapResponse(TResponse response);

    public async Task<LlmCompletionResult> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        using var httpRequest = BuildRequest(request);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach LLM provider at {BaseAddress}", httpClient.BaseAddress);
            throw new LlmProviderException(
                $"Failed to reach the LLM provider at '{httpClient.BaseAddress}'. Is it running?", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's token was not cancelled — this is HttpClient's own timeout firing.
            _logger.LogError("Request to LLM provider at {BaseAddress} timed out", httpClient.BaseAddress);
            throw new LlmProviderException(
                $"The request to the LLM provider at '{httpClient.BaseAddress}' timed out.");
        }

        using (httpResponse)
        {
            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "LLM provider returned {StatusCode} {ReasonPhrase}: {Body}",
                    (int)httpResponse.StatusCode,
                    httpResponse.ReasonPhrase,
                    errorBody);
                throw new LlmProviderException(
                    $"LLM provider returned {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}: {errorBody}");
            }

            TResponse? response;
            try
            {
                response = await httpResponse.Content.ReadFromJsonAsync<TResponse>(cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new LlmProviderException("Failed to deserialize the LLM provider's response.", ex);
            }

            if (response is null)
            {
                throw new LlmProviderException("LLM provider returned an empty response body.");
            }

            return MapResponse(response);
        }
    }
}
