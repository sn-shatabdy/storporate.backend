namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// Provider-agnostic entry point for text completion against whichever LLM backs the platform
/// (local Bionic today, a hosted provider later). Concrete providers live in
/// <c>Storporate.Infrastructure</c>; callers in modules/API code depend only on this interface.
/// </summary>
public interface ILlmClient
{
    Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken cancellationToken = default);
}
