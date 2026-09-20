using System.Text.Json;
using Microsoft.Extensions.Logging;
using Storporate.Infrastructure.Llm;

namespace Storporate.Modules.StudentGrowthExperience.Advisor;

/// <summary>
/// Parses and validates the JSON object the advisor LLM emits. Owns every
/// cap, every drop-rule, and every band-name normalization — once the parser
/// returns an <see cref="AdvisorParsedResponse"/> the rest of the pipeline
/// (processor, handlers, response shapes) treats the result as already
/// safe to store and serve.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why string-aware extraction.</b> The local Gemma reasoning model
/// routinely wraps its JSON in prose or fences, and sometimes emits a literal
/// <c>"x ] y"</c> inside a string value. The shared
/// <see cref="LlmJsonExtractor"/> handles both cases, so the parser
/// delegates the slicing rather than reimplementing bracket counting.
/// </para>
/// <para>
/// <b>Caps are truncations, not failures.</b> The plan's prompt budget
/// assumes the LLM never emits more than the model can fit, but the
/// Gemma reasoning model occasionally overshoots. Truncating (rather
/// than throwing) keeps the turn working; the dropped tail is always the
/// later questions / gaps / suggestions, which the next turn can recover
/// from a refresh.
/// </para>
/// <para>
/// <b>Validation failures throw.</b> An empty <c>reply</c> AND empty
/// <c>questions</c>, a question object missing its prompt, or a question
/// with no options at all — those throw <see cref="AdvisorResponseInvalidException"/>
/// because the model has produced something the UI literally cannot render.
/// The processor routes the throw through the retry/fail bookkeeper.
/// </para>
/// <para>
/// <b>Forbidden bands are dropped silently.</b> A <c>band</c> value that
/// isn't <see cref="AdvisorGapBands.Developing"/> or <see cref="AdvisorGapBands.Missing"/>
/// is normalized to <c>null</c> on the parsed gap rather than failing the
/// turn. The plan forbids arbitrary bands (Binding design rule: no numeric
/// scores); a gap with no band still reads naturally in the UI.
/// </para>
/// <para>
/// <b>Bad <c>sourceItemId</c> is dropped silently.</b> A <c>sourceItemId</c>
/// that's not a GUID, or that isn't in the candidate id set the prompt
/// supplied, is dropped — the suggestion keeps its title / reason / nextStep.
/// The processor logs a warning with the attempted id and the candidates
/// count (counts only — never the suggestion text). This is the
/// drop-source-keep-rest rule.
/// </para>
/// </remarks>
public static class AdvisorResponseParser
{
    /// <summary>Maximum questions kept on the parsed response. Surplus
    /// questions are truncated, never failed.</summary>
    public const int MaxQuestions = 5;

    /// <summary>Maximum options per question. Surplus options are truncated.</summary>
    public const int MaxOptionsPerQuestion = 6;

    /// <summary>Maximum gaps kept on the parsed summary.</summary>
    public const int MaxGaps = 8;

    /// <summary>Maximum suggestions kept on the parsed summary.</summary>
    public const int MaxSuggestions = 6;

    /// <summary>Maximum context notes kept on the parsed response.</summary>
    public const int MaxContextNotes = 8;

    /// <summary>Hard cap on the <c>reply</c> field, in characters. Sized to
    /// fit a multi-paragraph plain-text answer comfortably while keeping the
    /// stored row bounded.</summary>
    public const int MaxReplyCharacters = 6_000;

    /// <summary>Hard cap on the <c>title</c> field, in characters.</summary>
    public const int MaxTitleCharacters = 200;

    /// <summary>Hard cap on a single <c>contextNotes</c> entry, in characters.</summary>
    public const int MaxNoteCharacters = 300;

    /// <summary>Hard cap on the <c>changeNote</c> field, in characters.</summary>
    public const int MaxChangeNoteCharacters = 600;

    /// <summary>
    /// Parse and validate the LLM's <paramref name="outputText"/> into a
    /// typed <see cref="AdvisorParsedResponse"/>. Throws
    /// <see cref="AdvisorResponseInvalidException"/> when the model produced
    /// nothing the UI can render. Logger may be null — used only for the
    /// drop-source warning; callers that don't care can pass
    /// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance"/>.
    /// </summary>
    /// <param name="outputText">The provider's <c>OutputText</c>.</param>
    /// <param name="candidateIds">The set of feed-candidate ids the prompt
    /// supplied; an AI-emitted <c>sourceItemId</c> outside this set is
    /// dropped.</param>
    /// <param name="logger">Logger used for the drop-source warning.</param>
    public static AdvisorParsedResponse Parse(
        string? outputText,
        IReadOnlySet<Guid> candidateIds,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new AdvisorResponseInvalidException("Advisor LLM returned an empty response body.");
        }

        AdvisorJsonDto? dto;
        try
        {
            dto = LlmJsonExtractor.Deserialize<AdvisorJsonDto>(
                outputText, AdvisorJsonSerializerOptions.CamelCase);
        }
        catch (JsonException ex)
        {
            throw new AdvisorResponseInvalidException(
                "Advisor LLM response was not valid JSON: " + ex.Message, ex);
        }

        if (dto is null)
        {
            throw new AdvisorResponseInvalidException("Advisor LLM response deserialized to null.");
        }

        var reply = CapString(dto.Reply, MaxReplyCharacters);
        var title = CapString(dto.Title, MaxTitleCharacters);
        var questions = ParseQuestions(dto.Questions);
        var contextNotes = ParseContextNotes(dto.ContextNotes);
        var summary = ParseSummary(dto.Summary, candidateIds, logger);

        if (string.IsNullOrWhiteSpace(reply) && questions.Count == 0)
        {
            throw new AdvisorResponseInvalidException(
                "Advisor LLM response contained neither reply nor questions.");
        }

        return new AdvisorParsedResponse(
            Reply: string.IsNullOrWhiteSpace(reply) ? null : reply,
            Questions: questions,
            Title: string.IsNullOrWhiteSpace(title) ? null : title,
            ContextNotes: contextNotes,
            Summary: summary);
    }

    private static IReadOnlyList<AdvisorParsedQuestion> ParseQuestions(List<AdvisorJsonQuestion>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<AdvisorParsedQuestion>();
        }

        var questions = new List<AdvisorParsedQuestion>(capacity: Math.Min(raw.Count, MaxQuestions));
        foreach (var q in raw)
        {
            if (questions.Count >= MaxQuestions)
            {
                break;
            }
            if (q is null)
            {
                continue;
            }
            var prompt = CapString(q.Prompt, MaxReplyCharacters);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new AdvisorResponseInvalidException(
                    "Advisor LLM returned a question with no prompt.");
            }
            var options = new List<string>(capacity: Math.Min(q.Options?.Count ?? 0, MaxOptionsPerQuestion));
            if (q.Options is not null)
            {
                foreach (var option in q.Options)
                {
                    if (options.Count >= MaxOptionsPerQuestion)
                    {
                        break;
                    }
                    var trimmed = option?.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        continue;
                    }
                    options.Add(trimmed);
                }
            }
            if (options.Count == 0)
            {
                throw new AdvisorResponseInvalidException(
                    "Advisor LLM returned a question with no options.");
            }
            questions.Add(new AdvisorParsedQuestion(prompt, options));
        }

        return questions;
    }

    private static IReadOnlyList<string> ParseContextNotes(List<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<string>();
        }

        var notes = new List<string>(capacity: Math.Min(raw.Count, MaxContextNotes));
        foreach (var note in raw)
        {
            if (notes.Count >= MaxContextNotes)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(note))
            {
                continue;
            }
            var trimmed = note.Trim();
            notes.Add(CapString(trimmed, MaxNoteCharacters)!);
        }
        return notes;
    }

    private static AdvisorParsedSummary? ParseSummary(
        AdvisorJsonSummary? raw,
        IReadOnlySet<Guid> candidateIds,
        ILogger logger)
    {
        if (raw is null)
        {
            return null;
        }

        var gaps = ParseGaps(raw.Gaps);
        var suggestions = ParseSuggestions(raw.Suggestions, candidateIds, logger);
        var changeNote = CapString(raw.ChangeNote, MaxChangeNoteCharacters);

        if (gaps.Count == 0 && suggestions.Count == 0 && string.IsNullOrWhiteSpace(changeNote))
        {
            return null;
        }

        return new AdvisorParsedSummary(
            Gaps: gaps,
            Suggestions: suggestions,
            ChangeNote: string.IsNullOrWhiteSpace(changeNote) ? null : changeNote);
    }

    private static IReadOnlyList<AdvisorParsedGap> ParseGaps(List<AdvisorJsonGap>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<AdvisorParsedGap>();
        }

        var gaps = new List<AdvisorParsedGap>(capacity: Math.Min(raw.Count, MaxGaps));
        foreach (var g in raw)
        {
            if (gaps.Count >= MaxGaps)
            {
                break;
            }
            if (g is null)
            {
                continue;
            }
            var title = CapString(g.Title, MaxTitleCharacters);
            var detail = CapString(g.Detail, MaxReplyCharacters);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(detail))
            {
                continue;
            }
            var band = NormalizeBand(g.Band);
            gaps.Add(new AdvisorParsedGap(title, detail, band));
        }
        return gaps;
    }

    private static IReadOnlyList<AdvisorParsedSuggestion> ParseSuggestions(
        List<AdvisorJsonSuggestion>? raw,
        IReadOnlySet<Guid> candidateIds,
        ILogger logger)
    {
        if (raw is null || raw.Count == 0)
        {
            return Array.Empty<AdvisorParsedSuggestion>();
        }

        var suggestions = new List<AdvisorParsedSuggestion>(capacity: Math.Min(raw.Count, MaxSuggestions));
        var droppedCount = 0;
        foreach (var s in raw)
        {
            if (suggestions.Count >= MaxSuggestions)
            {
                break;
            }
            if (s is null)
            {
                continue;
            }
            var title = CapString(s.Title, MaxTitleCharacters);
            var reason = CapString(s.Reason, MaxReplyCharacters);
            var nextStep = CapString(s.NextStep, MaxReplyCharacters);
            if (string.IsNullOrWhiteSpace(title)
                || string.IsNullOrWhiteSpace(reason)
                || string.IsNullOrWhiteSpace(nextStep))
            {
                continue;
            }

            Guid? source = null;
            if (!string.IsNullOrWhiteSpace(s.SourceItemId))
            {
                if (Guid.TryParse(s.SourceItemId, out var parsed)
                    && candidateIds.Contains(parsed))
                {
                    source = parsed;
                }
                else
                {
                    droppedCount++;
                }
            }

            suggestions.Add(new AdvisorParsedSuggestion(title, reason, nextStep, source));
        }

        if (droppedCount > 0)
        {
            // Counts only — never the suggestion text. The plan forbids logging
            // AI output, and the dropped id is potentially the AI's misreading
            // of a candidate we sent it.
            logger.LogWarning(
                "Advisor dropped {DroppedCount} suggestion source(s) for being absent from the candidate set (candidates: {CandidatesCount}).",
                droppedCount, candidateIds.Count);
        }

        return suggestions;
    }

    private static string? NormalizeBand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var trimmed = raw.Trim();
        return string.Equals(trimmed, AdvisorGapBands.Developing, StringComparison.Ordinal)
            || string.Equals(trimmed, AdvisorGapBands.Missing, StringComparison.Ordinal)
            ? trimmed
            : null;
    }

    private static string? CapString(string? value, int maxCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        return trimmed.Length <= maxCharacters
            ? trimmed
            : trimmed[..maxCharacters];
    }
}
