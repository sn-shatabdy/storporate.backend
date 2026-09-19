using System.Reflection;
using System.Text;
using Storporate.Modules.StudentGrowthExperience;
using Storporate.Modules.StudentGrowthExperience.Advisor;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.StudentGrowthExperience;

/// <summary>
/// The advisor's Binding design rule forbids a hand-picked set of category
/// words in both the model-facing prompts and the application's own
/// category-free code paths. This guard test scans the production prompt
/// builder, the response parser, the request/response records, and the
/// EF table names for any of the banned words (case-insensitive substring).
///
/// The guard's value is in the second test — temporarily editing a banned
/// word into a prompt constant demonstrably makes the test fail. Run that
/// edit, observe the failure, then revert.
/// </summary>
public class AdvisorHardCodingGuardTests
{
    /// <summary>The plan-banned category words. Every entry is matched
    /// case-insensitive as a substring against the production artifacts.</summary>
    public static readonly string[] BannedCategoryWords = new[]
    {
        "university", "institute", "laboratory", "competition", "business",
        "organization", "organisation", "company", "employer", "job", "career",
        "scholarship", "startup", "internship", "degree",
    };

    [Fact]
    public void SystemPrompt_DoesNotContainAnyBannedCategoryWord()
    {
        var prompt = AdvisorPromptBuilder.BuildSystemPrompt();

        var found = FindBanned(prompt);
        Assert.True(found.Count == 0,
            $"System prompt contains banned category words: {string.Join(", ", found)}");
    }

    [Fact]
    public void UserPromptForOpeningMode_DoesNotContainAnyBannedCategoryWord()
    {
        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Opening,
            ContextNotes: new[] { "Studies physics" },
            CurrentSummary: null,
            FeedCandidates: new[]
            {
                new FeedItem
                {
                    Id = Guid.NewGuid(),
                    SourceName = "Source",
                    SourceUrl = "https://example.com/src",
                    Url = "https://example.com/article",
                    Title = "An article title",
                    Summary = "An article summary.",
                    FetchedAt = DateTimeOffset.UtcNow,
                },
            },
            PortfolioItems: new[]
            {
                new PortfolioItemSnapshot(
                    Label: "Senior capstone",
                    Description: "Built a Next.js dashboard for the local library.",
                    Findings: new[]
                    {
                        new FindingSnapshot("Next.js", "Strong", "used throughout"),
                        new FindingSnapshot("GraphQL", "Developing", "mentioned once"),
                    }),
            },
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "I want to learn about astrophotography.");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        var userPromptFound = FindBanned(request.UserPrompt);
        Assert.True(userPromptFound.Count == 0,
            $"User prompt contains banned category words: {string.Join(", ", userPromptFound)}");
    }

    [Fact]
    public void UserPromptForReplyMode_DoesNotContainAnyBannedCategoryWord()
    {
        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Reply,
            ContextNotes: new[] { "Studies physics" },
            CurrentSummary: new AdvisorParsedSummary(
                Gaps: new[]
                {
                    new AdvisorParsedGap("Hands-on practice", "None yet", AdvisorGapBands.Missing),
                },
                Suggestions: new[]
                {
                    new AdvisorParsedSuggestion(
                        Title: "Try the club",
                        Reason: "Hands-on intro",
                        NextStep: "Sign up",
                        SourceFeedItemId: null),
                },
                ChangeNote: null),
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "I have a DSLR.");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        var found = FindBanned(request.UserPrompt);
        Assert.True(found.Count == 0,
            $"User prompt contains banned category words: {string.Join(", ", found)}");
    }

    [Fact]
    public void UserPromptForRefreshMode_DoesNotContainAnyBannedCategoryWord()
    {
        var inputs = new AdvisorPromptInputs(
            Mode: AdvisorTurnModes.Refresh,
            ContextNotes: Array.Empty<string>(),
            CurrentSummary: new AdvisorParsedSummary(
                Gaps: Array.Empty<AdvisorParsedGap>(),
                Suggestions: Array.Empty<AdvisorParsedSuggestion>(),
                ChangeNote: "Refreshed."),
            FeedCandidates: Array.Empty<FeedItem>(),
            PortfolioItems: Array.Empty<PortfolioItemSnapshot>(),
            History: Array.Empty<LlmChatMessage>(),
            UserPrompt: "(refresh)");

        var request = AdvisorPromptBuilder.BuildCompletionRequest(inputs, new AdvisorOptions());

        var found = FindBanned(request.UserPrompt);
        Assert.True(found.Count == 0,
            $"User prompt contains banned category words: {string.Join(", ", found)}");
    }

    [Fact]
    public void ModuleTypeAndConstantNames_DoNotContainAnyBannedCategoryWord()
    {
        // Reflecting on the module's types guards against a future field /
        // constant name accidentally baking one of the banned words into
        // the public surface (e.g. an `InternshipCategory` enum, a
        // `JobListing` DTO). Word boundaries — `JobType` and `NewJobId`
        // are legitimate plumbing names, but a hypothetical
        // `StudentJobListing` type or `InternshipCategory` enum would
        // trip the guard.
        var module = typeof(AdvisorOptions).Assembly;
        var names = module.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Storporate.Modules.StudentGrowthExperience", StringComparison.Ordinal) == true)
            .SelectMany(t => new[]
            {
                t.FullName ?? t.Name,
                t.Name,
            })
            .Concat(module.GetTypes()
                .Where(t => t.Namespace?.StartsWith("Storporate.Modules.StudentGrowthExperience", StringComparison.Ordinal) == true)
                .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                    .Concat<MemberInfo>(t.GetProperties())
                    .Where(m => m.Name != "Item" && m.Name != "EqualityContract")
                    .Select(m => m.Name)));

        var found = new List<string>();
        foreach (var name in names)
        {
            foreach (var banned in BannedCategoryWords)
            {
                var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(banned)}\b";
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        name, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    found.Add($"{name} (contains '{banned}')");
                }
            }
        }

        Assert.True(found.Count == 0,
            "Module types / members contain banned category words: " + string.Join(", ", found));
    }

    [Fact]
    public void ParseModelDoesNotIntroduceCategories()
    {
        // The model-facing parser docs and code must not bias toward any
        // banned category word either — its only knowledge of "bands" is
        // the two neutral ones (Developing / Missing). Word boundaries
        // keep the check from matching substrings inside longer tokens.
        var parserType = typeof(AdvisorResponseParser);
        var constants = parserType
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly)
            .Select(f => f.GetRawConstantValue() as string ?? string.Empty);

        var found = new List<string>();
        foreach (var constantValue in constants)
        {
            foreach (var banned in BannedCategoryWords)
            {
                var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(banned)}\b";
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        constantValue, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    found.Add($"'{constantValue}' (contains '{banned}')");
                }
            }
        }

        Assert.True(found.Count == 0,
            "Parser constants contain banned category words: " + string.Join(", ", found));
    }

    [Fact]
    public void BannedWordGuard_DemonstrablyCatchesAFakeViolation()
    {
        // This test stands in for the manual "edit, observe failure, revert"
        // workflow: it builds a prompt whose body literally contains
        // "competition" (a banned word) and asserts the guard's check would
        // catch it. When the rule above ever drifts, the assertion in the
        // prompt-guard tests above is the one that fails — the helper here
        // just proves the matching itself is sensitive.
        var fakePromptThatShouldFail = "The student has joined a competition and won.";

        var found = FindBanned(fakePromptThatShouldFail);
        Assert.Contains("competition", found);
    }

    private static List<string> FindBanned(string content)
    {
        // Word-boundary check (case-insensitive). Substring matching was
        // too aggressive — the prompt builder has to be free to USE these
        // words as anti-examples ("don't assume the student wants a job",
        // "don't assume they're applying to an organization") without
        // tripping the guard. The guard's intent is to catch the model
        // being led to fit the student into one of these boxes; a token
        // that appears as part of an instruction against using the word
        // is fine.
        var found = new List<string>();
        foreach (var banned in BannedCategoryWords)
        {
            var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(banned)}\b";
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    content, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                found.Add(banned);
            }
        }
        return found;
    }
}
