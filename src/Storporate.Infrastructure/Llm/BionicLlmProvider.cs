using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Infrastructure.Llm;

/// <summary>
/// <see cref="ILlmClient"/> backed by the locally-running Bionic server (LM-Studio family),
/// serving Gemma 4 12B QAT over an OpenAI-compatible <c>/chat/completions</c> endpoint. Endpoint
/// shape, model id, and the reasoning-model quirk below were all confirmed live in Phase 1.
/// </summary>
public sealed class BionicLlmProvider : HttpLlmClientBase<BionicChatResponse>
{
    private readonly BionicOptions _options;

    public BionicLlmProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<BionicOptions> options,
        ILogger<BionicLlmProvider> logger)
        : base(httpClientFactory, logger)
    {
        _options = options.Value;
    }

    protected override string HttpClientName => LlmHttpClientNames.Bionic;

    protected override HttpRequestMessage BuildRequest(LlmCompletionRequest request)
    {
        // Emit messages in the order the OpenAI/Bionic chat API expects: the system
        // message (when present), every entry of the supplied History list verbatim
        // (in the order the caller passed them — neither re-ordered nor de-duplicated
        // here), then the user prompt as the final turn. A null or empty History
        // collapses to the pre-STOR-40 shape "[system?, user]" so every existing
        // single-turn caller keeps working without any per-caller wiring change.
        var messages = new List<BionicChatRequestMessage>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new BionicChatRequestMessage("system", request.SystemPrompt));
        }

        if (request.History is not null)
        {
            foreach (var entry in request.History)
            {
                messages.Add(new BionicChatRequestMessage(entry.Role, entry.Content));
            }
        }

        messages.Add(new BionicChatRequestMessage("user", request.UserPrompt));

        // The Bionic server does not auto-select a model even with just-in-time loading on —
        // the model id must always be sent explicitly (confirmed Phase 1).
        var payload = new BionicChatRequestPayload(_options.ModelId, messages, request.MaxOutputTokens);

        return new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
    }

    protected override LlmCompletionResult MapResponse(BionicChatResponse response)
    {
        var choice = response.Choices?.Count > 0
            ? response.Choices[0]
            : throw new LlmProviderException("LLM provider response contained no choices.");

        var message = choice.Message
            ?? throw new LlmProviderException("LLM provider response choice contained no message.");

        // Only ever read `content` — never `reasoning_content` (the model's internal
        // chain-of-thought). At a too-low max_tokens, the full budget can be consumed by
        // reasoning, leaving `content` empty with finish_reason "length"; surface that as a
        // clear, actionable error instead of returning a silently-empty completion.
        if (string.IsNullOrEmpty(message.Content))
        {
            throw new LlmProviderException(
                "LLM provider returned an empty completion (finish_reason: "
                + $"'{choice.FinishReason}'). This reasoning model can spend its entire token "
                + "budget on internal reasoning before producing a final answer — increase "
                + $"{nameof(LlmCompletionRequest)}.{nameof(LlmCompletionRequest.MaxOutputTokens)}.");
        }

        var usage = response.Usage is null
            ? null
            : new LlmTokenUsage(response.Usage.PromptTokens, response.Usage.CompletionTokens, response.Usage.TotalTokens);

        return new LlmCompletionResult(message.Content, response.Model ?? _options.ModelId, usage);
    }
}
