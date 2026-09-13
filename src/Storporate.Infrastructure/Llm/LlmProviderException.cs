namespace Storporate.Infrastructure.Llm;

/// <summary>
/// Wraps any failure talking to an LLM provider — non-success HTTP status, network/timeout
/// failure, or an unparsable/unmappable response — into one clear exception type. Callers never
/// see a raw <see cref="HttpRequestException"/> or JSON exception; they see this, which the API's
/// <c>GlobalExceptionHandler</c> maps to a clean error response.
/// </summary>
public sealed class LlmProviderException : Exception
{
    public LlmProviderException(string message)
        : base(message)
    {
    }

    public LlmProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
