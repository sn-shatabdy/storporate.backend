using System.Text.Json.Serialization;

namespace Storporate.Infrastructure.Llm;

// Request payload — OpenAI-compatible chat-completions shape, as confirmed live against Bionic
// in Phase 1: POST {BaseUrl}/chat/completions with {"model", "messages":[{"role","content"}], "max_tokens"}.

public sealed record BionicChatRequestPayload(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<BionicChatRequestMessage> Messages,
    [property: JsonPropertyName("max_tokens")] int MaxTokens);

public sealed record BionicChatRequestMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

// Response shape. IMPORTANT: this is a reasoning model — `message.reasoning_content` carries its
// internal chain-of-thought ahead of the real answer in `message.content`. Only `content` is ever
// surfaced to callers (see BionicLlmProvider.MapResponse); `reasoning_content` is deserialized
// here only so it doesn't fail strict parsing, never read.

public sealed record BionicChatResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<BionicChatChoice>? Choices,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("usage")] BionicChatUsage? Usage);

public sealed record BionicChatChoice(
    [property: JsonPropertyName("message")] BionicChatMessage? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

public sealed record BionicChatMessage(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent,
    [property: JsonPropertyName("role")] string? Role);

public sealed record BionicChatUsage(
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens);
