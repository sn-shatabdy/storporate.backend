namespace Storporate.SharedKernel.Abstractions;

/// <summary>Token usage for a single completion, when the provider reports it.</summary>
public sealed record LlmTokenUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens);
