namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// The result of a completed <see cref="ILlmClient.CompleteAsync"/> call.
/// </summary>
/// <param name="OutputText">
/// The provider's final answer text. Providers must map only the real answer here — never a
/// reasoning model's internal chain-of-thought (see <see cref="LlmCompletionRequest.MaxOutputTokens"/>).
/// </param>
/// <param name="ModelUsed">The model id that actually served the request.</param>
/// <param name="Usage">Token usage reported by the provider, when available.</param>
public sealed record LlmCompletionResult(
    string OutputText,
    string ModelUsed,
    LlmTokenUsage? Usage = null);
