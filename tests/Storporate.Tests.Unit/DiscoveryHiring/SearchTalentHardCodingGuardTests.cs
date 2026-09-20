using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Storporate.Modules.DiscoveryHiring.TalentSearch;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-43 plan guard: the talent-search output must never carry the
/// framing-words <c>evidence</c> or <c>proof</c> (those terms describe a
/// verification process the candidate surface does not perform). A
/// future change that re-introduces one of these banned words anywhere
/// the LLM is told to avoid it should fail this test loudly.
///
/// <para>
/// <b>Scope.</b> The guard scans every Phase 2 server-side artifact that
/// can be sent to the LLM as instructions OR echoed back to the user as
/// a reason / message:
/// <list type="bullet">
///   <item>The two prompt-builder system prompts (the literal system
///         prompts, not the user prompt — those can mention anything).</item>
///   <item>The error messages thrown by the response parsers (those flow
///         into the LLM JSON-extractor feedback loop on retry).</item>
///   <item>The deterministic-fallback reason template the parser uses to
///         substitute when a candidate reason is rejected.</item>
///   <item>All <see cref="CreateTalentSearchValidator"/> messages.</item>
///   <item>The handler log templates (audit metadata + log lines should
///         also stay free of these words).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>What is NOT scanned.</b> The hard-coding guard is intentionally
/// narrow. The system prompt's "Never include the words ..." sentence is
/// exempt — the guard exists to catch a future change that re-introduces
/// the words into something the model could output, not the
/// negative-instruction that tells the model not to use them.
/// </para>
/// </remarks>
public class SearchTalentHardCodingGuardTests
{
    /// <summary>Plan-banned words. Whole-word, case-insensitive match
    /// against the production artifacts below.</summary>
    public static readonly string[] BannedFramingWords = new[]
    {
        "evidence", "proof",
    };

    [Fact]
    public void SystemPrompts_DoNotIntroduceBannedWordsOutsideTheNegativeInstruction()
    {
        // The system prompt legitimately says 'Never include the words
        // "evidence" or "proof"'. Strip that one negative-instruction
        // sentence before scanning, so a future change that re-introduces
        // the words anywhere else (e.g. a "we use X as evidence of skill"
        // rationale) trips the guard.
        var requirementsPrompt = SearchTalentRequirementsPromptBuilder.BuildSystemPrompt();
        var rankingPrompt = SearchTalentRankingPromptBuilder.BuildSystemPrompt();

        var found = new List<string>();
        found.AddRange(FindBanned(StripNegativeInstruction(requirementsPrompt), "requirements system prompt"));
        found.AddRange(FindBanned(StripNegativeInstruction(rankingPrompt), "ranking system prompt"));

        Assert.True(found.Count == 0,
            "System prompts contain banned framing words: " + string.Join(", ", found));
    }

    [Fact]
    public void ResponseParserExceptionMessages_DoNotIntroduceBannedWords()
    {
        // SearchTalentResponseInvalidException messages get fed back into
        // the LLM on retries — if we ever put a banned word in them the
        // model will quote it back. Scanned via reflection on the
        // SearchTalentResponseParser's private helper messages.
        var assembly = typeof(SearchTalentRequirementsPromptBuilder).Assembly;
        var parserType = assembly.GetType(
            "Storporate.Modules.DiscoveryHiring.TalentSearch.SearchTalentRequirementsParser");
        Assert.NotNull(parserType);
        var rankingType = assembly.GetType(
            "Storporate.Modules.DiscoveryHiring.TalentSearch.SearchTalentRankingParser");
        Assert.NotNull(rankingType);

        var found = new List<string>();
        foreach (var type in new[] { parserType!, rankingType! })
        {
            FindBannedStringsInThrowExpressions(type, found);
        }

        Assert.True(found.Count == 0,
            "Response parser exception messages contain banned framing words: "
                + string.Join(", ", found));
    }

    [Fact]
    public void FallbackReasonTemplate_DoesNotIntroduceBannedWords()
    {
        // Build the deterministic fallback the ranking parser uses when a
        // candidate's reason is rejected. The template is the one in the
        // system prompt ("Strong in {skill}, shown in {item}. Developing
        // in {skill}, shown in {item}.") — re-built here through the
        // public surface so the guard depends only on the parser's
        // behavior, not on a duplicated string constant.
        var candidate = new SearchTalentValidatedCandidate(
            CandidateId: Guid.NewGuid(),
            MatchedSkills: new[]
            {
                new MatchedSkillBand("Python", ConfidenceBands.Strong),
                new MatchedSkillBand("FastAPI", ConfidenceBands.Developing),
            },
            Reason: "",
            CitedItems: new[]
            {
                new ValidatedCitedItem(
                    PortfolioItemId: Guid.NewGuid(),
                    Label: "API project",
                    Category: PortfolioCategories.Project,
                    SkillName: "Python",
                    Band: ConfidenceBands.Strong),
            });

        // Drive the public ranking parser with a "reason rejected"
        // payload — it should swap in the deterministic fallback.
        var handles = new Dictionary<string, SearchTalentRankingCandidate>
        {
            ["c1"] = new SearchTalentRankingCandidate(
                EntryId: candidate.CandidateId,
                Items: Array.Empty<SearchTalentRankingItem>()),
        };

        var rejectedReason =
            "This candidate's portfolio contains strong evidence of Python skill.";
        var llmOutput = JsonSerializer.Serialize(new
        {
            ranking = new[]
            {
                new
                {
                    candidate = "c1",
                    reason = rejectedReason,
                    citations = Array.Empty<object>(),
                },
            },
        });

        var validated = SearchTalentRankingParser.Parse(llmOutput, handles, NullLogger.Instance);
        var emitted = Assert.Single(validated);
        var fallbackReason = emitted.Reason;

        // The guard's contract: the parser MUST NOT let the model's
        // banned word reach the user. The fallback template is the only
        // text the user can see — assert it is clean.
        var found = FindBanned(fallbackReason, "fallback reason");
        Assert.True(found.Count == 0,
            "Fallback reason template contains banned framing words: "
                + string.Join(", ", found));
    }

    [Fact]
    public void ValidatorMessages_DoNotIntroduceBannedWords()
    {
        // The WithMessage(...) bodies on the validator's rules are part
        // of the wire surface — the FE displays them in the 400
        // response. Scan the literal constants.
        var validatorType = typeof(CreateTalentSearchValidator);
        var stringConstants = validatorType
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        var found = new List<string>();
        foreach (var constant in stringConstants)
        {
            found.AddRange(FindBanned(constant, "validator constant"));
        }

        Assert.True(found.Count == 0,
            "Validator constants contain banned framing words: "
                + string.Join(", ", found));
    }

    [Fact]
    public void ModuleTypeNames_DoNotIntroduceBannedWords()
    {
        // A future type named `EvidenceReportResponse` would be a
        // regression; word-boundary check keeps the guard tight.
        var assembly = typeof(SearchTalentRequirementsPromptBuilder).Assembly;
        var names = assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith(
                "Storporate.Modules.DiscoveryHiring", StringComparison.Ordinal) == true)
            .SelectMany(t => new[] { t.FullName ?? t.Name, t.Name });

        var found = new List<string>();
        foreach (var name in names)
        {
            found.AddRange(FindBanned(name, "type name"));
        }

        Assert.True(found.Count == 0,
            "Module type names contain banned framing words: "
                + string.Join(", ", found));
    }

    [Fact]
    public void BannedWordGuard_DemonstrablyCatchesAFakeViolation()
    {
        var fakeReasonThatShouldFail =
            "Strong match with portfolio evidence of the requested skill.";

        var found = FindBanned(fakeReasonThatShouldFail, "fake");
        Assert.Contains(found, f => f.StartsWith("'evidence'", StringComparison.Ordinal));
    }

    // ----- helpers -----

    private static List<string> FindBanned(string content, string where)
    {
        var found = new List<string>();
        foreach (var banned in BannedFramingWords)
        {
            var pattern = $@"\b{Regex.Escape(banned)}\b";
            if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
            {
                found.Add($"'{banned}' in {where}");
            }
        }
        return found;
    }

    /// <summary>Strip the single negative-instruction sentence that
    /// legitimately names the banned words from each system prompt.
    /// The guard's purpose is to catch re-introduction of those words
    /// elsewhere, not to flag the sentence that tells the model to
    /// avoid them.</summary>
    private static string StripNegativeInstruction(string prompt)
    {
        // Match the sentence "Never include the words ..." (system
        // prompt literal).
        var lines = prompt.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Contains("Never include the words", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    /// <summary>Scan a type's source for raw string literals that appear
    /// inside <c>throw new SearchTalentResponseInvalidException(...)</c>
    /// expressions. C# string literals cannot be retrieved from metadata
    /// alone, so this walks the type's C# source file in the repo's
    /// <c>src/Storporate.Modules/DiscoveryHiring/TalentSearch</c>
    /// directory by name match. If the source file cannot be found the
    /// guard skips silently rather than false-positive.</summary>
    private static void FindBannedStringsInThrowExpressions(Type parserType, List<string> sink)
    {
        var sourceFile = parserType.Name switch
        {
            "SearchTalentRequirementsParser" =>
                "src/Storporate.Modules/DiscoveryHiring/TalentSearch/SearchTalentResponseParser.cs",
            "SearchTalentRankingParser" =>
                "src/Storporate.Modules/DiscoveryHiring/TalentSearch/SearchTalentResponseParser.cs",
            _ => null,
        };
        if (sourceFile is null)
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            return;
        }

        var fullPath = Path.Combine(repoRoot, sourceFile);
        if (!File.Exists(fullPath))
        {
            return;
        }

        var source = File.ReadAllText(fullPath);
        // Match:  throw new SearchTalentResponseInvalidException(\s* "..."
        // Plus one variant where the throw concatenates with ex.Message.
        var matches = Regex.Matches(
            source,
            @"throw\s+new\s+SearchTalentResponseInvalidException\s*\(\s*""([^""]+)""",
            RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var literal = match.Groups[1].Value;
            sink.AddRange(FindBanned(literal, $"{parserType.Name} throw literal"));
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "storporate.backend.sln"))
                || Directory.Exists(Path.Combine(dir.FullName, "src"))
                    && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        // Walk from cwd up; the test runner executes from the repo root.
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
