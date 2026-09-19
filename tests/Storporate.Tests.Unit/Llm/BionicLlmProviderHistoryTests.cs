using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Llm;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Tests.Unit.Llm;

/// <summary>
/// STOR-40 Phase 1: pin the wire payload shape the <see cref="BionicLlmProvider"/> sends
/// when a multi-turn <see cref="LlmCompletionRequest.History"/> list is supplied. The
/// provider must emit <c>[system?, history[0], history[1], ..., user]</c> in that exact
/// order so the model sees the prior turns ahead of the new user message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an HTTP-level test and not a unit test of <c>BuildRequest</c>.</b>
/// <c>BuildRequest</c> is <c>protected</c> on <see cref="HttpLlmClientBase{TResponse}"/>;
/// the only way to exercise it end-to-end without spinning up a fake HTTP listener is
/// to drive the provider through its named <see cref="HttpClient"/> with a capturing
/// <see cref="HttpMessageHandler"/>. The handler records the JSON body the provider
/// serialized into the request, so the assertions below are exactly the wire shape a
/// live Bionic server would receive — not a side-effect of an internal helper.
/// </para>
/// <para>
/// <b>Why not just deserialize the raw payload into <c>BionicChatRequestPayload</c>.</b>
/// That's the same type the provider uses to serialize, so deserializing into it would
/// prove only that round-tripping works. Asserting the raw <c>messages</c> array via
/// <see cref="JsonDocument"/> verifies the exact wire shape — including the role strings
/// and the order — independent of the provider's own DTO.
/// </para>
/// </remarks>
public class BionicLlmProviderHistoryTests
{
    private const string SystemPrompt = "You are a career advisor.";
    private const string UserPrompt = "What should I learn next?";

    [Fact]
    public async Task BuildRequest_WithSystemAndHistoryAndUser_EmitsSystemThenHistoryThenUserInOrder()
    {
        // History list deliberately alternates user / assistant so the test catches a
        // future bug where the provider silently flips role labels or re-orders the
        // turns before serializing.
        var history = new List<LlmChatMessage>
        {
            new("user", "What skills do I have?"),
            new("assistant", "You've demonstrated React and Public Speaking."),
            new("user", "What about TypeScript?"),
            new("assistant", "No TypeScript evidence in your portfolio yet."),
        };

        var recorder = await SendAsync(history);

        using var document = JsonDocument.Parse(recorder.CapturedBody!);
        var root = document.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray().ToList();

        // Exact order: system, m1, m2, m3, m4, user.
        Assert.Equal(6, messages.Count);

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(SystemPrompt, messages[0].GetProperty("content").GetString());

        for (var i = 0; i < history.Count; i++)
        {
            Assert.Equal(history[i].Role, messages[i + 1].GetProperty("role").GetString());
            Assert.Equal(history[i].Content, messages[i + 1].GetProperty("content").GetString());
        }

        Assert.Equal("user", messages[5].GetProperty("role").GetString());
        Assert.Equal(UserPrompt, messages[5].GetProperty("content").GetString());

        // Sanity-check the other top-level fields the Bionic payload contract carries.
        Assert.Equal("google/gemma-4-12b-qat", root.GetProperty("model").GetString());
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task BuildRequest_WithHistoryButNoSystemPrompt_EmitsHistoryThenUserInOrder()
    {
        // The system message slot must be omitted entirely when SystemPrompt is null
        // — a stale-system-prompt bug would carry an unwanted system: "" entry in the
        // wire payload and the model would respond as if instructed by nothing.
        var history = new List<LlmChatMessage>
        {
            new("user", "first"),
            new("assistant", "second"),
        };

        var recorder = await SendAsync(history, systemPrompt: null);

        using var document = JsonDocument.Parse(recorder.CapturedBody!);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToList();

        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("first", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("second", messages[1].GetProperty("content").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal(UserPrompt, messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task BuildRequest_WithNullHistory_EmitsOnlySystemAndUser()
    {
        // Regression guard for the single-turn path: a null History must collapse to
        // exactly [system?, user] — never an empty messages array, never a stray
        // null entries. This is the path every existing pre-STOR-40 caller uses.
        var recorder = await SendAsync(history: null, systemPrompt: SystemPrompt);

        using var document = JsonDocument.Parse(recorder.CapturedBody!);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(SystemPrompt, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(UserPrompt, messages[1].GetProperty("content").GetString());
    }

    /// <summary>
    /// Build a <see cref="BionicLlmProvider"/> wired to a capturing
    /// <see cref="HttpMessageHandler"/>, drive one completion, and return the
    /// request body the handler recorded.
    /// </summary>
    private static async Task<RecordingHandler> SendAsync(
        IReadOnlyList<LlmChatMessage>? history,
        string? systemPrompt = SystemPrompt)
    {
        var recorder = new RecordingHandler();

        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        // Use a one-off factory that always returns a HttpClient backed by the recorder,
        // regardless of the name the provider asks for. The provider only ever asks
        // for the Bionic-named client, so naming the recorder client that name keeps
        // production plumbing identical to the real call. The BaseAddress is set
        // exactly as the production AddBionicLlmProvider does — the provider sends a
        // relative "chat/completions" path that resolves against BaseAddress.
        services.AddHttpClient(LlmHttpClientNames.Bionic, httpClient =>
        {
            httpClient.BaseAddress = new Uri("http://localhost:1234/v1/");
            httpClient.Timeout = TimeSpan.FromSeconds(120);
        })
        .ConfigurePrimaryHttpMessageHandler(() => recorder);
        services.AddSingleton(Options.Create(new BionicOptions
        {
            BaseUrl = "http://localhost:1234/v1",
            ModelId = "google/gemma-4-12b-qat",
        }));
        services.AddTransient<ILlmClient, BionicLlmProvider>();

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<ILlmClient>();

        var request = new LlmCompletionRequest(
            UserPrompt: UserPrompt,
            SystemPrompt: systemPrompt,
            MaxOutputTokens: 512,
            History: history);

        _ = await client.CompleteAsync(request, CancellationToken.None);

        return recorder;
    }

    /// <summary>
    /// Minimal <see cref="HttpMessageHandler"/> that records the request body and
    /// returns a valid <c>BionicChatResponse</c> body. Returning a valid response is
    /// important — the provider's <c>MapResponse</c> runs after <c>SendAsync</c> and
    /// any unmarshallable / empty response body would throw before the assertion below
    /// can read the captured payload.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri;
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            // Return the smallest valid BionicChatResponse body — the provider's
            // MapResponse pulls `choices[0].message.content` only.
            var json = """
                       {
                         "id": "test-id",
                         "object": "chat.completion",
                         "created": 1700000000,
                         "model": "google/gemma-4-12b-qat",
                         "choices": [
                           {
                             "index": 0,
                             "message": {
                               "role": "assistant",
                               "content": "ok"
                             },
                             "finish_reason": "stop"
                           }
                         ],
                         "usage": {
                           "prompt_tokens": 10,
                           "completion_tokens": 1,
                           "total_tokens": 11
                         }
                       }
                       """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
        }
    }
}