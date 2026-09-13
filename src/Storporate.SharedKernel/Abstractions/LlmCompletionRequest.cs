namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// A provider-agnostic request for a single text completion.
/// </summary>
/// <param name="UserPrompt">The user-facing prompt to complete.</param>
/// <param name="SystemPrompt">An optional system/instruction prompt, when the caller needs one.</param>
/// <param name="MaxOutputTokens">
/// The maximum number of output tokens the provider may generate. Defaults to 512, not a small
/// number: at least one deployed model (Bionic's Gemma reasoning model) spends part of this
/// budget on internal chain-of-thought before producing a final answer, and a low value can
/// exhaust the budget before any answer is emitted (see <see cref="LlmCompletionResult"/>).
/// </param>
public sealed record LlmCompletionRequest(
    string UserPrompt,
    string? SystemPrompt = null,
    int MaxOutputTokens = 512);
