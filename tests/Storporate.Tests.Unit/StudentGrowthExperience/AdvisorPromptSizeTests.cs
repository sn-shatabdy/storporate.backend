using Storporate.Modules.StudentGrowthExperience;
using Storporate.Modules.StudentGrowthExperience.Advisor;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.StudentGrowthExperience;

/// <summary>
/// Sizing coverage for <see cref="AdvisorPromptBuilder"/> against a worst-
/// case seeding of 200 portfolio items and 40 prior messages. The
/// acceptance criterion: the built user prompt must stay within
/// <see cref="AdvisorOptions.MaxUserPromptCharacters"/>, the rolling
/// history must stay within <c>HistoryWindowTurns × 2</c>, and the most
/// recent messages must be the ones included.
/// </summary>
public class AdvisorPromptSizeTests
{
    [Fact]
    public void BuildCompletionRequest_LargePortfolioAndHistory_RespectsBudgetAndWindow()
    {
        var portfolioItems = new List<PortfolioItemSnapshot>();
        // Reverse iteration: build i = 199 first so the LIST is
        // newest-first — the prompt-builder appends items in the order the
        // processor hands them. The newest (i=199) MUST appear in the
        // prompt; the oldest (i=0) MAY be dropped under budget.
        for (var i = 199; i >= 0; i--)
        {
            portfolioItems.Add(new PortfolioItemSnapshot(
                Label: $"Item-{i:000}",
                Description: string.Concat(Enumerable.Repeat(
                    "This portfolio item shows thoughtful work on a project involving React, GraphQL, and Next.js. ",
                    30)),
                Findings: new[]
                {
                    new FindingSnapshot("React", "Strong", "Builds component-based UIs."),
                }));
        }

        var history = new List<LlmChatMessage>();
        for (var i = 0; i < 40; i++)
        {
            history.Add(new LlmChatMessage("user", $"history user {i}: " + new string('u', 100)));
            history.Add(new LlmChatMessage("assistant", $"history assistant {i}: " + new string('a', 100)));
        }

        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Reply,
            ContextNotes: Enumerable.Range(0, 8).Select(i => $"note-{i}").ToArray(),
            CurrentSummary: new AdvisorParsedSummary(
                Gaps: new[]
                {
                    new AdvisorParsedGap("Hands-on practice", "None yet", AdvisorGapBands.Missing),
                },
                Suggestions: new[]
                {
                    new AdvisorParsedSuggestion(
                        Title: "Build something",
                        Reason: "Apply what you've learned",
                        NextStep: "Pick a tiny project",
                        SourceFeedItemId: null),
                },
                ChangeNote: null),
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: portfolioItems,
            History: history,
            UserPrompt: "the newest student turn");

        var options = new AdvisorOptions
        {
            MaxUserPromptCharacters = 24_000,
            HistoryWindowTurns = 6,
            MaxHistoryEntryCharacters = 3_000,
            MaxPortfolioItemCharacters = 4_000,
        };

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, options);

        // 1. Budget respected — the full user prompt fits the character cap.
        Assert.True(
            request.UserPrompt.Length <= options.MaxUserPromptCharacters,
            $"User prompt length {request.UserPrompt.Length} exceeded budget {options.MaxUserPromptCharacters}.");

        // 2. History window respected — at most HistoryWindowTurns × 2 entries.
        Assert.NotNull(request.History);
        Assert.Equal(options.HistoryWindowTurns * 2, request.History!.Count);

        // 3. The most recent history entries are present; the oldest ones are not.
        Assert.Contains(request.History, h => h.Content.StartsWith("history user 39"));
        Assert.Contains(request.History, h => h.Content.StartsWith("history assistant 39"));
        Assert.DoesNotContain(request.History, h => h.Content.StartsWith("history user 0"));
        Assert.DoesNotContain(request.History, h => h.Content.StartsWith("history assistant 32"));

        // 4. The newest portfolio item (Item-199) is present in the prompt —
        // items are appended newest-first, so it's either included whole or
        // truncated, but it's never missing entirely.
        Assert.Contains("Item-199", request.UserPrompt);

        // 5. The student's current message appears in the STUDENT MESSAGE
        // section — either fully, or truncated with the truncation marker
        // if the budget was exhausted. Either way it must not be missing
        // (the section is always appended, even with truncation).
        Assert.True(
            request.UserPrompt.Contains("the newest student turn")
            || request.UserPrompt.Contains("…[truncated]"),
            "Student message must be present (or visible via truncation marker) in the prompt.");
    }

    [Fact]
    public void BuildCompletionRequest_TruncatesPortfolioItemIfLargerThanMaxPortfolioItemCharacters()
    {
        var oversized = new PortfolioItemSnapshot(
            Label: "Item",
            Description: new string('x', 20_000),
            Findings: Array.Empty<FindingSnapshot>());

        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Reply,
            ContextNotes: Array.Empty<string>(),
            CurrentSummary: null,
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: new[] { oversized },
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "u");

        var options = new AdvisorOptions
        {
            MaxPortfolioItemCharacters = 1_000,
        };

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, options);

        Assert.Contains("…[truncated]", request.UserPrompt);
    }
}
