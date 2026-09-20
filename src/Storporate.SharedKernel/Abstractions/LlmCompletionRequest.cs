namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// A single prior turn in a multi-turn LLM conversation — used by callers that need
/// to follow up on an earlier assistant reply (the STOR-40 advisor page is the first
/// such caller; the existing single-turn
/// <see cref="LlmCompletionRequest.UserPrompt"/>-only path stays the default).
/// </summary>
/// <param name="Role">
/// The OpenAI/Bionic chat role. The provider passes the value through to the wire
/// payload unchanged; the only roles this codebase ever emits are <c>"user"</c> and
/// <c>"assistant"</c>, but the type is a free-form string so a future <c>"tool"</c>
/// or <c>"function"</c> call works without an enum amendment.
/// </param>
/// <param name="Content">The text content of the message. Providers serialize the value
/// verbatim into the <c>messages[N].content</c> field — there is no current need to
/// support multi-modal content parts, and an extension record can add that later
/// without breaking the wire shape we serialize today.</param>
public sealed record LlmChatMessage(string Role, string Content);

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
/// <param name="History">
/// Optional ordered list of prior turns to feed the model alongside the <see cref="UserPrompt"/>.
/// The provider emits the messages in this order: <c>system?</c> (when
/// <see cref="SystemPrompt"/> is non-empty), then each entry of <see cref="History"/> in
/// the supplied order, then the <see cref="UserPrompt"/> as the final
/// <c>"user"</c> turn. When <see cref="History"/> is null or empty the wire payload is
/// exactly the same shape as before this property was added — single-turn callers are
/// unaffected. The <c>Role</c> values are forwarded as-passed (<c>"user"</c>,
/// <c>"assistant"</c>); no re-ordering or de-duplication happens here.
/// </param>
public sealed record LlmCompletionRequest(
    string UserPrompt,
    string? SystemPrompt = null,
    int MaxOutputTokens = 512,
    IReadOnlyList<LlmChatMessage>? History = null);
