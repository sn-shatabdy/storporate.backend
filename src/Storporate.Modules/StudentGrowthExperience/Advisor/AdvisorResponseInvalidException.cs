namespace Storporate.Modules.StudentGrowthExperience.Advisor;

/// <summary>
/// Thrown by <see cref="AdvisorResponseParser"/> when the LLM's JSON object
/// violates a hard rule — no <c>reply</c> and no <c>questions</c>, a
/// question object missing its <c>prompt</c>, etc. Caught by
/// <c>AdvisorTurnJobProcessor</c> and routed through
/// <see cref="Storporate.Infrastructure.Jobs.JobBookkeeper.RequeueOrFailAsync"/>
/// the same way as <see cref="Storporate.Infrastructure.Llm.LlmProviderException"/>
/// — the same retry budget applies because the contract violation is treated
/// as a transient model quirk rather than a permanent data error.
/// </summary>
/// <remarks>
/// The exception's <see cref="Message"/> is intentionally short and free of
/// the LLM's raw output text — the retry/fail path logs the message but
/// never the raw body, matching the STOR-40 "don't log prompts, student text,
/// or AI output" rule.
/// </remarks>
public sealed class AdvisorResponseInvalidException : Exception
{
    public AdvisorResponseInvalidException(string message)
        : base(message)
    {
    }

    public AdvisorResponseInvalidException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
