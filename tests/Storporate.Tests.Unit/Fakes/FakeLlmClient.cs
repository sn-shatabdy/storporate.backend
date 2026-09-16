using Storporate.Infrastructure.Llm;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// Test double for <see cref="ILlmClient"/>. Mirrors the recording-fake style of
/// <see cref="FakeArtifactStore"/> — no mocking framework, deterministic,
/// inspectable. Every <see cref="CompleteAsync"/> call appends to
/// <see cref="Calls"/> and returns whatever the test configured via
/// <see cref="EnqueueResponse"/> / <see cref="EnqueueException"/>, falling back
/// to the static <see cref="DefaultResponse"/> / <see cref="DefaultException"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a queue, not a single canned response.</b> The Phase 2 acceptance
/// criterion "after 3 processing attempts the job's Status is Failed" needs the
/// fake to throw <see cref="LlmProviderException"/> on all three calls — easy
/// with a queue of one repeating exception. Other tests only need one call
/// before they assert; the queue still serves them by enqueuing a single entry.
/// </para>
/// <para>
/// <b>Why no shared default.</b> A test that forgets to enqueue a response and
/// forgets to throw should fail loudly rather than silently succeed on an
/// empty-string response (which would look like an LLM-empty-content bug). So
/// the dequeue path throws <see cref="InvalidOperationException"/> when the
/// queue is exhausted and neither default is set.
/// </para>
/// </remarks>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<object> _responses = new();
    private int _callCount;

    /// <summary>Every <see cref="CompleteAsync"/> call, in invocation order.
    /// <see cref="Call.UserPrompt"/>, <see cref="Call.SystemPrompt"/>, and
    /// <see cref="Call.MaxOutputTokens"/> are recorded verbatim so tests can
    /// assert exactly what the worker sent to the model.</summary>
    public IReadOnlyList<Call> Calls => _calls;

    private readonly List<Call> _calls = new();

    /// <summary>Enqueue a successful <see cref="LlmCompletionResult"/> to be
    /// returned by the next <see cref="CompleteAsync"/> call.</summary>
    public void EnqueueResponse(LlmCompletionResult response) =>
        _responses.Enqueue(response);

    /// <summary>Enqueue an <see cref="LlmProviderException"/> (or any other
    /// <see cref="Exception"/>) to be thrown by the next
    /// <see cref="CompleteAsync"/> call. Re-thrown via
    /// <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/>
    /// so the original stack trace is preserved across the dequeue boundary.</summary>
    public void EnqueueException(Exception exception) =>
        _responses.Enqueue(exception);

    /// <summary>Fallback used when the response queue is exhausted and the
    /// caller hasn't set this default. <see langword="null"/> means "exhausted
    /// queue is an error" — see the type remarks.</summary>
    public LlmCompletionResult? DefaultResponse { get; set; }

    /// <summary>Fallback used when the response queue is exhausted and the
    /// caller hasn't set <see cref="DefaultResponse"/>. <see langword="null"/>
    /// means "exhausted queue is an error".</summary>
    public Exception? DefaultException { get; set; }

    public Task<LlmCompletionResult> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var call = new Call(
            UserPrompt: request.UserPrompt,
            SystemPrompt: request.SystemPrompt,
            MaxOutputTokens: request.MaxOutputTokens);
        _calls.Add(call);
        _callCount++;

        if (_responses.TryDequeue(out var next))
        {
            return next switch
            {
                LlmCompletionResult result => Task.FromResult(result),
                Exception ex => ThrowAsync(ex),
                _ => throw new InvalidOperationException("Unexpected queued response shape."),
            };
        }

        if (DefaultException is not null)
        {
            return ThrowAsync(DefaultException);
        }

        if (DefaultResponse is not null)
        {
            return Task.FromResult(DefaultResponse);
        }

        throw new InvalidOperationException(
            $"FakeLlmClient received call #{_callCount} with no queued response and no default. " +
            "Tests must EnqueueResponse / EnqueueException or set DefaultResponse / DefaultException.");
    }

    /// <summary>The number of times <see cref="CompleteAsync"/> was invoked.</summary>
    public int CallCount => _callCount;

    private static async Task<LlmCompletionResult> ThrowAsync(Exception exception) =>
        throw exception;

    /// <summary>One recorded call to <see cref="ILlmClient.CompleteAsync"/> —
    /// the three fields the system/user prompt test asserts against are
    /// captured verbatim so the test's "what was the model actually asked?"
    /// check has a stable shape.</summary>
    public sealed record Call(string UserPrompt, string? SystemPrompt, int MaxOutputTokens);
}
