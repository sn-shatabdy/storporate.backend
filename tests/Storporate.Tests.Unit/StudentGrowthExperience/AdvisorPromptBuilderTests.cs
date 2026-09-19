using System.Text.Json;
using Storporate.Modules.StudentGrowthExperience;
using Storporate.Modules.StudentGrowthExperience.Advisor;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.StudentGrowthExperience;

/// <summary>
/// Pure-function coverage for <see cref="AdvisorPromptBuilder"/>. No DI,
/// no DbContext — every input is a hand-built <see cref="AdvisorPromptInputs"/>
/// record. The acceptance criteria these tests pin:
///
/// <list type="bullet">
///   <item><b>System prompt shape.</b> The system prompt carries the strict
///   JSON contract, the honesty rules, and the untrusted-data rule.</item>
///   <item><b>User prompt sections.</b> The user prompt carries MODE, then
///   STUDENT CONTEXT NOTES, then CURRENT SUMMARY (Reply/Refresh only),
///   then FEED CANDIDATES, then PORTFOLIO ITEMS, then STUDENT MESSAGE — in
///   that priority order.</item>
///   <item><b>History limit.</b> The history window is capped at
///   <c>HistoryWindowTurns × 2</c>; older entries are dropped.</item>
///   <item><b>Budget respected.</b> The final prompt never exceeds
///   <see cref="AdvisorOptions.MaxUserPromptCharacters"/>.</item>
///   <item><b>Feed candidates not in prompt.</b> A feed candidate id is
///   NEVER in the prompt; only its source/title/summary are.</item>
///   <item><b>sourceItemId round-trip.</b> A suggestion source that exists
///   in the candidate set is preserved as a guid in the suggestion list
///   rendered for the model.</item>
/// </list>
/// </summary>
public class AdvisorPromptBuilderTests
{
    [Fact]
    public void BuildSystemPrompt_ContainsTheStrictJsonContract()
    {
        var prompt = AdvisorPromptBuilder.BuildSystemPrompt();

        Assert.Contains("reply", prompt);
        Assert.Contains("questions", prompt);
        Assert.Contains("title", prompt);
        Assert.Contains("contextNotes", prompt);
        Assert.Contains("summary", prompt);
        Assert.Contains("gaps", prompt);
        Assert.Contains("suggestions", prompt);
        Assert.Contains("sourceItemId", prompt);
        Assert.Contains("changeNote", prompt);

        // Honesty rule: only what's in the student's record.
        Assert.Contains("invent achievements", prompt, StringComparison.OrdinalIgnoreCase);

        // Format rule: no markdown, no scoring.
        Assert.Contains("Plain text only", prompt);
        Assert.Contains("No numeric scores", prompt);
    }

    [Fact]
    public void BuildCompletionRequest_ForOpening_DoesNotIncludeCurrentSummarySection()
    {
        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Opening,
            ContextNotes: Array.Empty<string>(),
            CurrentSummary: SampleSummary(), // present, but Opening ignores it
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "I want to learn about astrophotography.");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        Assert.Contains("Mode: Opening", request.UserPrompt);
        Assert.DoesNotContain("CURRENT SUMMARY", request.UserPrompt);
        Assert.Contains("STUDENT MESSAGE", request.UserPrompt);
        Assert.Contains("I want to learn about astrophotography.", request.UserPrompt);
    }

    [Fact]
    public void BuildCompletionRequest_ForReply_IncludesCurrentSummarySection()
    {
        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Reply,
            ContextNotes: new[] { "Studies physics" },
            CurrentSummary: SampleSummary(),
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "I have access to a DSLR.");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        Assert.Contains("CURRENT SUMMARY", request.UserPrompt);
        Assert.Contains("Build a small tracker", request.UserPrompt); // from sample
    }

    [Fact]
    public void BuildCompletionRequest_ForRefresh_IncludesCurrentSummarySection()
    {
        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Refresh,
            ContextNotes: Array.Empty<string>(),
            CurrentSummary: SampleSummary(),
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "(refresh)");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        Assert.Contains("Mode: Refresh", request.UserPrompt);
        Assert.Contains("CURRENT SUMMARY", request.UserPrompt);
    }

    [Fact]
    public void BuildCompletionRequest_SectionsInPriorityOrder()
    {
        var feedId = Guid.NewGuid();
        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Reply,
            ContextNotes: new[] { "first note" },
            CurrentSummary: SampleSummary(),
            FeedCandidates: new[] { SampleFeedItem(feedId, "src", "title-X", "summary-X") },
            PortfolioItems: new[] { new PortfolioItemSnapshot("p-label", null, Array.Empty<FindingSnapshot>()) },
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "the user prompt");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        var modeIdx = request.UserPrompt.IndexOf("--- MODE ---", StringComparison.Ordinal);
        var notesIdx = request.UserPrompt.IndexOf("STUDENT CONTEXT NOTES", StringComparison.Ordinal);
        var summaryIdx = request.UserPrompt.IndexOf("CURRENT SUMMARY", StringComparison.Ordinal);
        var feedIdx = request.UserPrompt.IndexOf("FEED CANDIDATES", StringComparison.Ordinal);
        var portfolioIdx = request.UserPrompt.IndexOf("PORTFOLIO ITEMS", StringComparison.Ordinal);
        var messageIdx = request.UserPrompt.IndexOf("STUDENT MESSAGE", StringComparison.Ordinal);

        Assert.True(modeIdx >= 0);
        Assert.True(notesIdx > modeIdx);
        Assert.True(summaryIdx > notesIdx);
        Assert.True(feedIdx > summaryIdx);
        Assert.True(portfolioIdx > feedIdx);
        Assert.True(messageIdx > portfolioIdx);
    }

    [Fact]
    public void BuildCompletionRequest_FeedCandidateUrlNotEmbeddedButTitleAndSummaryAre()
    {
        var feedId = Guid.NewGuid();
        var feed = SampleFeedItem(
            Id: feedId,
            SourceName: "Example Daily",
            Title: "Astrophotography club meeting",
            Summary: "Hands-on session about star tracking.");

        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Opening,
            ContextNotes: Array.Empty<string>(),
            CurrentSummary: null,
            FeedCandidates: new[] { feed },
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "I want to learn astrophotography.");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        Assert.Contains("Astrophotography club meeting", request.UserPrompt);
        Assert.Contains("Hands-on session", request.UserPrompt);
        // Server-side: the article URL never leaves the prompt.
        Assert.DoesNotContain("https://feed.example.com/article", request.UserPrompt);
    }

    [Fact]
    public void BuildCompletionRequest_HistoryWindowRespectsCap()
    {
        var history = new List<LlmChatMessage>();
        for (var i = 0; i < 30; i++)
        {
            history.Add(new LlmChatMessage("user", $"turn-{i}"));
            history.Add(new LlmChatMessage("assistant", $"reply-{i}"));
        }

        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Reply,
            ContextNotes: Array.Empty<string>(),
            CurrentSummary: null,
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: history,
            UserPrompt: "newest turn");

        var options = new AdvisorOptions { HistoryWindowTurns = 3 };
        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, options);

        Assert.NotNull(request.History);
        // HistoryWindowTurns × 2 = 6 most recent.
        Assert.Equal(6, request.History!.Count);
        Assert.Equal("turn-27", request.History[0].Content);
        Assert.Equal("reply-29", request.History[5].Content);
    }

    [Fact]
    public void BuildCompletionRequest_UserPromptWithinBudget()
    {
        var items = new List<PortfolioItemSnapshot>();
        for (var i = 0; i < 50; i++)
        {
            items.Add(new PortfolioItemSnapshot(
                Label: $"Item-{i}",
                Description: string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 60)),
                Findings: new[]
                {
                    new FindingSnapshot("React", "Strong", "used throughout"),
                    new FindingSnapshot("GraphQL", "Developing", "mentioned once"),
                }));
        }

        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Reply,
            ContextNotes: Enumerable.Range(0, 12).Select(i => $"note {i}").ToArray(),
            CurrentSummary: SampleSummary(),
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: items,
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "the user prompt");

        var options = new AdvisorOptions { MaxUserPromptCharacters = 8_000 };
        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, options);

        Assert.True(request.UserPrompt.Length <= options.MaxUserPromptCharacters,
            $"User prompt length {request.UserPrompt.Length} exceeded {options.MaxUserPromptCharacters}.");
    }

    [Fact]
    public void BuildCompletionRequest_SourceItemIdInCurrentSummaryRoundTripsInPrompt()
    {
        var feedId = Guid.NewGuid();
        var summary = new AdvisorParsedSummary(
            Gaps: Array.Empty<AdvisorParsedGap>(),
            Suggestions: new[]
            {
                new AdvisorParsedSuggestion(
                    Title: "Try the club",
                    Reason: "Hands-on intro",
                    NextStep: "Sign up by Friday",
                    SourceFeedItemId: feedId),
            },
            ChangeNote: null);

        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Reply,
            ContextNotes: Array.Empty<string>(),
            CurrentSummary: summary,
            FeedCandidates: new[] { SampleFeedItem(feedId, "src", "title", "summary") },
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "ok");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        // The source's GUID appears in the summary's source line, not the article URL.
        Assert.Contains(feedId.ToString(), request.UserPrompt);
        Assert.Contains("source:", request.UserPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCompletionRequest_NoHistoryReturnsNullHistory()
    {
        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Opening,
            ContextNotes: Array.Empty<string>(),
            CurrentSummary: null,
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "first turn");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        Assert.Null(request.History);
    }

    [Fact]
    public void BuildCompletionRequest_MaxOutputTokensFromOptions()
    {
        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Opening,
            ContextNotes: Array.Empty<string>(),
            CurrentSummary: null,
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "first turn");

        var options = new AdvisorOptions { MaxOutputTokens = 2048 };
        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, options);

        Assert.Equal(2048, request.MaxOutputTokens);
    }

    private static AdvisorParsedSummary SampleSummary() =>
        new(
            Gaps: new[]
            {
                new AdvisorParsedGap("Hands-on practice", "Never built a tracker", AdvisorGapBands.Missing),
            },
            Suggestions: new[]
            {
                new AdvisorParsedSuggestion(
                    Title: "Build a small tracker",
                    Reason: "First project",
                    NextStep: "Buy a star chart",
                    SourceFeedItemId: null),
            },
            ChangeNote: null);

    private static FeedItem SampleFeedItem(Guid Id, string SourceName, string Title, string Summary) =>
        new()
        {
            Id = Id,
            SourceName = SourceName,
            SourceUrl = "https://feed.example.com/source",
            Url = "https://feed.example.com/article",
            Title = Title,
            Summary = Summary,
            FetchedAt = DateTimeOffset.UtcNow,
        };
}
