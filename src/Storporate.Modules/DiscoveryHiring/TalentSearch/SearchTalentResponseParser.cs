using System.Text.Json;
using Microsoft.Extensions.Logging;
using Storporate.Infrastructure.Llm;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.TalentSearch;

/// <summary>
/// Thrown by <see cref="SearchTalentRequirementsParser"/> and
/// <see cref="SearchTalentRankingParser"/> when the LLM output cannot be
/// coerced into the contract the rest of the pipeline expects. The
/// processor routes these throws through its graceful-fallback path
/// (vector order with deterministic reasons); they are NOT retryable
/// failures because the model has produced something the validator
/// literally cannot interpret.
/// </summary>
public sealed class SearchTalentResponseInvalidException : Exception
{
    public SearchTalentResponseInvalidException(string message)
        : base(message)
    {
    }

    public SearchTalentResponseInvalidException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// One ranked (or unranked-tail) candidate after server-side validation
/// of the LLM output. <see cref="MatchedSkills"/> / <see cref="CitedItems"/>
/// are derived from real, currently-existing data in the
/// <c>TalentIndexEntry</c> snapshot — never from the LLM's raw text.
/// </summary>
public sealed record SearchTalentValidatedCandidate(
    Guid CandidateId,
    IReadOnlyList<MatchedSkillBand> MatchedSkills,
    string Reason,
    IReadOnlyList<ValidatedCitedItem> CitedItems);

/// <summary>One matched skill on a candidate. <see cref="Band"/> comes
/// from the snapshot, never from model text.</summary>
public sealed record MatchedSkillBand(string Name, string Band);

/// <summary>One cited item backing a candidate's reason.</summary>
public sealed record ValidatedCitedItem(
    Guid PortfolioItemId,
    string Label,
    string Category,
    string SkillName,
    string Band);

/// <summary>
/// Parses the requirements-extraction LLM call into a typed
/// <see cref="SearchTalentExtractedRequirements"/>. Throws
/// <see cref="SearchTalentResponseInvalidException"/> on unrecoverable
/// contract violations (empty body, no JSON). The processor catches the
/// throw and falls back to the raw query.
/// </summary>
public static class SearchTalentRequirementsParser
{
    public sealed record SearchTalentExtractedRequirements(
        IReadOnlyList<string> Skills,
        string? Summary);

    public static SearchTalentExtractedRequirements Parse(string? outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new SearchTalentResponseInvalidException(
                "Requirements LLM returned an empty response body.");
        }

        SearchTalentRequirementsDto? dto;
        try
        {
            dto = LlmJsonExtractor.Deserialize<SearchTalentRequirementsDto>(
                outputText, SearchTalentJsonSerializerOptions.CamelCase);
        }
        catch (JsonException ex)
        {
            throw new SearchTalentResponseInvalidException(
                "Requirements LLM response was not valid JSON: " + ex.Message, ex);
        }

        if (dto is null)
        {
            throw new SearchTalentResponseInvalidException(
                "Requirements LLM response deserialized to null.");
        }

        var rawSkills = dto.Skills ?? new List<string>();
        var trimmedSkills = new List<string>(capacity: Math.Min(rawSkills.Count, TalentSearchDefaults.MaxExtractedSkills));
        foreach (var skill in rawSkills)
        {
            if (trimmedSkills.Count >= TalentSearchDefaults.MaxExtractedSkills)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(skill))
            {
                continue;
            }
            var trimmed = skill.Trim();
            if (trimmed.Length > TalentSearchDefaults.MaxExtractedSkillLength)
            {
                trimmed = trimmed[..TalentSearchDefaults.MaxExtractedSkillLength];
            }
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                trimmedSkills.Add(trimmed);
            }
        }

        string? summary = null;
        if (!string.IsNullOrWhiteSpace(dto.Summary))
        {
            var summaryTrimmed = dto.Summary.Trim();
            if (summaryTrimmed.Length > TalentSearchDefaults.MaxRequirementsSummaryLength)
            {
                summaryTrimmed = summaryTrimmed[..TalentSearchDefaults.MaxRequirementsSummaryLength];
            }
            summary = summaryTrimmed;
        }

        return new SearchTalentExtractedRequirements(
            Skills: trimmedSkills,
            Summary: summary);
    }

    private sealed class SearchTalentRequirementsDto
    {
        public List<string>? Skills { get; set; }
        public string? Summary { get; set; }
    }
}

/// <summary>
/// Parses the ranking LLM call into a list of <see cref="SearchTalentValidatedCandidate"/>s,
/// with every citation validated against the live candidate snapshot.
/// The order is whatever the model produced (after the unknown-handle /
/// duplicate-handle drops); the processor appends unranked candidates in
/// vector order after the parsed list and trims the whole thing to
/// <see cref="TalentSearchDefaults.ResultLimit"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drop rules (silent).</b>
/// <list type="bullet">
///   <item>Unknown candidate handle (<c>c99</c> etc.) — dropped.</item>
///   <item>Duplicate candidate handle — only the first occurrence is kept.</item>
///   <item>Citation whose <c>portfolioItemId</c> isn't an item id in that candidate's snapshot — dropped.</item>
///   <item>Citation whose <c>skillName</c> doesn't match a skill of that item (case-insensitive) — dropped.</item>
/// </list>
/// All four rules log a warning with counts only — never with the model's
/// text — to honour the plan's audit-friendly logging rule.
/// </para>
/// <para>
/// <b>Reason substitution.</b> A reason that contains the banned words
/// <c>evidence</c> / <c>proof</c> (case-insensitive, whole-word), is
/// empty, or has zero valid citations left after the drop rules is
/// replaced with the deterministic fallback built from the candidate's
/// matched skills. The plan keeps the same fallback shape the prompt's
/// example used: <c>"Strong in {skill}, shown in {item}. Developing in
/// {skill}, shown in {item}."</c> with up to three clauses.
/// </para>
/// </remarks>
public static class SearchTalentRankingParser
{
    /// <summary>Parse and validate the LLM's ranking output.</summary>
    /// <param name="outputText">The provider's <c>OutputText</c>.</param>
    /// <param name="candidateHandles">Map of <c>c1..cN</c> → live candidate snapshot.</param>
    /// <param name="logger">Logger used for the drop-rule warnings.</param>
    public static IReadOnlyList<SearchTalentValidatedCandidate> Parse(
        string? outputText,
        IReadOnlyDictionary<string, SearchTalentRankingCandidate> candidateHandles,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(candidateHandles);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new SearchTalentResponseInvalidException(
                "Ranking LLM returned an empty response body.");
        }

        SearchTalentRankingDto? dto;
        try
        {
            dto = LlmJsonExtractor.Deserialize<SearchTalentRankingDto>(
                outputText, SearchTalentJsonSerializerOptions.CamelCase);
        }
        catch (JsonException ex)
        {
            throw new SearchTalentResponseInvalidException(
                "Ranking LLM response was not valid JSON: " + ex.Message, ex);
        }

        var rankedList = dto?.Ranking ?? new List<SearchTalentRankingEntryDto>();

        var validated = new List<SearchTalentValidatedCandidate>(capacity: rankedList.Count);
        var seenHandles = new HashSet<string>(StringComparer.Ordinal);
        var unknownHandles = 0;
        var emptyReasons = 0;
        var bannedReasons = 0;
        var droppedCitations = 0;
        var zeroCitationReasons = 0;

        foreach (var entry in rankedList)
        {
            if (entry is null)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.Candidate))
            {
                continue;
            }
            var handle = entry.Candidate.Trim();
            if (!candidateHandles.TryGetValue(handle, out var candidate))
            {
                unknownHandles++;
                continue;
            }
            if (!seenHandles.Add(handle))
            {
                // Duplicate: keep the first occurrence, drop the rest silently.
                continue;
            }

            // Build the lookup table for citation validation: item id → item snapshot.
            var itemsById = candidate.Items.ToDictionary(i => i.PortfolioItemId);

            var validCitations = new List<ValidatedCitedItem>();
            if (entry.Citations is not null)
            {
                foreach (var citation in entry.Citations)
                {
                    if (citation is null)
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(citation.PortfolioItemId)
                        || string.IsNullOrWhiteSpace(citation.SkillName))
                    {
                        droppedCitations++;
                        continue;
                    }
                    if (!Guid.TryParse(citation.PortfolioItemId, out var itemId))
                    {
                        droppedCitations++;
                        continue;
                    }
                    if (!itemsById.TryGetValue(itemId, out var item))
                    {
                        droppedCitations++;
                        continue;
                    }
                    var matchedSkill = item.Skills.FirstOrDefault(s =>
                        string.Equals(s.Name, citation.SkillName, StringComparison.OrdinalIgnoreCase));
                    if (matchedSkill is null)
                    {
                        droppedCitations++;
                        continue;
                    }
                    validCitations.Add(new ValidatedCitedItem(
                        PortfolioItemId: item.PortfolioItemId,
                        Label: item.Label,
                        Category: item.Category,
                        SkillName: matchedSkill.Name,
                        Band: matchedSkill.Band));
                }
            }

            // Compute the matched skills (distinct name+band pairs) from
            // valid citations. When the model returned zero valid
            // citations, fall back to every Strong/Developing skill the
            // candidate carries — see BuildDeterministicReason.
            IReadOnlyList<MatchedSkillBand> matchedSkills = validCitations
                .Select(c => new MatchedSkillBand(c.SkillName, c.Band))
                .GroupBy(ms => (ms.Name, ms.Band), ms => ms)
                .Select(g => g.First())
                .ToList();

            var trimmedReason = entry.Reason?.Trim() ?? string.Empty;
            if (trimmedReason.Length > TalentSearchDefaults.MaxReasonLength)
            {
                trimmedReason = trimmedReason[..TalentSearchDefaults.MaxReasonLength];
            }

            string finalReason;
            if (string.IsNullOrWhiteSpace(trimmedReason))
            {
                emptyReasons++;
                finalReason = BuildDeterministicReason(candidate, matchedSkills);
            }
            else if (ContainsBannedWord(trimmedReason))
            {
                bannedReasons++;
                finalReason = BuildDeterministicReason(candidate, matchedSkills);
            }
            else if (validCitations.Count == 0)
            {
                zeroCitationReasons++;
                matchedSkills = ExtractFallbackMatchedSkills(candidate);
                finalReason = BuildDeterministicReason(candidate, matchedSkills);
            }
            else
            {
                finalReason = trimmedReason;
            }

            validated.Add(new SearchTalentValidatedCandidate(
                CandidateId: candidate.EntryId,
                MatchedSkills: matchedSkills,
                Reason: finalReason,
                CitedItems: validCitations));
        }

        // Counts only — never the LLM's text. The plan forbids logging
        // AI output, and a malformed citation id is potentially the
        // AI's misreading of an id we sent it.
        if (unknownHandles > 0)
        {
            logger.LogWarning(
                "Ranking parser dropped {DroppedCount} unknown-handle entries.",
                unknownHandles);
        }
        if (emptyReasons > 0)
        {
            logger.LogWarning(
                "Ranking parser replaced {ReplacedCount} empty reasons with deterministic fallback.",
                emptyReasons);
        }
        if (bannedReasons > 0)
        {
            logger.LogWarning(
                "Ranking parser replaced {ReplacedCount} banned-word reasons with deterministic fallback.",
                bannedReasons);
        }
        if (zeroCitationReasons > 0)
        {
            logger.LogWarning(
                "Ranking parser replaced {ReplacedCount} zero-citation reasons with deterministic fallback.",
                zeroCitationReasons);
        }
        if (droppedCitations > 0)
        {
            logger.LogWarning(
                "Ranking parser dropped {DroppedCount} invalid citations (item id or skill name not in candidate snapshot).",
                droppedCitations);
        }

        return validated;
    }

    /// <summary>Word-boundary check for the plan's banned words. The
    /// guard test asserts that no server-generated string in the
    /// pipeline ever contains <c>evidence</c> or <c>proof</c>; this is
    /// the runtime enforcement at the parser boundary.</summary>
    private static bool ContainsBannedWord(string text)
    {
        var pattern = @"\b(evidence|proof)\b";
        return System.Text.RegularExpressions.Regex.IsMatch(
            text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>The deterministic fallback reason: band word + skill
    /// name + item label, up to three clauses. Built from the candidate's
    /// snapshot only — never from LLM text.</summary>
    private static string BuildDeterministicReason(
        SearchTalentRankingCandidate candidate,
        IReadOnlyList<MatchedSkillBand> matchedSkills)
    {
        var skills = matchedSkills.Count > 0
            ? matchedSkills
            : ExtractFallbackMatchedSkills(candidate);

        var clauses = new List<string>(capacity: Math.Min(skills.Count, 3));
        foreach (var skill in skills)
        {
            if (clauses.Count >= 3)
            {
                break;
            }
            // Find the first item (by snapshot order) that has this skill.
            var item = candidate.Items.FirstOrDefault(i =>
                i.Skills.Any(s => string.Equals(s.Name, skill.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.Band, skill.Band, StringComparison.OrdinalIgnoreCase)));
            if (item is null)
            {
                continue;
            }
            var bandWord = string.Equals(skill.Band, ConfidenceBands.Strong, StringComparison.OrdinalIgnoreCase)
                ? "Strong"
                : "Developing";
            clauses.Add($"{bandWord} in {skill.Name}, shown in \"{item.Label}\".");
        }

        if (clauses.Count == 0)
        {
            // Last resort: no skills at all. The candidate only exists in the index
            // because of an item that has zero Strong/Developing findings — fall
            // back to a one-clause note. Still no banned words.
            return "Listed for an item with no Strong or Developing findings yet.";
        }

        return string.Join(' ', clauses);
    }

    /// <summary>The "fallback" matched-skill list: every Strong /
    /// Developing skill the candidate carries, Strong first then by name.
    /// Used when the LLM gave us a reason without valid citations.</summary>
    private static IReadOnlyList<MatchedSkillBand> ExtractFallbackMatchedSkills(
        SearchTalentRankingCandidate candidate)
    {
        var all = candidate.Items
            .SelectMany(i => i.Skills)
            .Where(s => string.Equals(s.Band, ConfidenceBands.Strong, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Band, ConfidenceBands.Developing, StringComparison.OrdinalIgnoreCase))
            .Select(s => new MatchedSkillBand(s.Name, s.Band))
            .Distinct()
            .ToList();

        return all
            .OrderByDescending(s => string.Equals(s.Band, ConfidenceBands.Strong, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class SearchTalentRankingDto
    {
        public List<SearchTalentRankingEntryDto>? Ranking { get; set; }
    }

    private sealed class SearchTalentRankingEntryDto
    {
        public string? Candidate { get; set; }
        public string? Reason { get; set; }
        public List<SearchTalentRankingCitationDto>? Citations { get; set; }
    }

    private sealed class SearchTalentRankingCitationDto
    {
        public string? PortfolioItemId { get; set; }
        public string? SkillName { get; set; }
    }
}
