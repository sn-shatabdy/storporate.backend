using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Storporate.Modules.StudentGrowthExperience.Advisor;

namespace Storporate.Tests.Unit.StudentGrowthExperience;

/// <summary>
/// Coverage for <see cref="AdvisorResponseParser"/>. The parser's caps and
/// drop-rules are the only defenses the rest of the pipeline has against a
/// pathological LLM response (oversized, malformed, invented bands, fake
/// feed sources). The tests below pin every cap, every drop rule, and
/// every validation failure the plan specifies.
/// </summary>
public class AdvisorResponseParserTests
{
    [Fact]
    public void Parse_EmptyResponseBody_Throws()
    {
        Assert.Throws<AdvisorResponseInvalidException>(() =>
            AdvisorResponseParser.Parse(
                "",
                new HashSet<Guid>(),
                NullLogger.Instance));
    }

    [Fact]
    public void Parse_NeitherReplyNorQuestions_Throws()
    {
        var json = """{"title":"no body","contextNotes":["x"]}""";

        Assert.Throws<AdvisorResponseInvalidException>(() =>
            AdvisorResponseParser.Parse(
                json,
                new HashSet<Guid>(),
                NullLogger.Instance));
    }

    [Fact]
    public void Parse_OnlyReply_Succeeds()
    {
        var json = """{"reply":"hello"}""";

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.Equal("hello", parsed.Reply);
        Assert.Empty(parsed.Questions);
    }

    [Fact]
    public void Parse_OnlyQuestions_Succeeds()
    {
        var json = """
        {
          "questions": [
            { "prompt": "What matters most?", "options": ["speed", "depth"] }
          ]
        }
        """;

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.Null(parsed.Reply);
        var q = Assert.Single(parsed.Questions);
        Assert.Equal("What matters most?", q.Prompt);
        Assert.Equal(new[] { "speed", "depth" }, q.Options);
    }

    [Fact]
    public void Parse_TruncatesToFiveQuestions()
    {
        var json = JsonSerializer.Serialize(new
        {
            reply = "ok",
            questions = Enumerable.Range(0, 8).Select(i => new
            {
                prompt = $"q{i}",
                options = new[] { "a", "b" },
            }).ToArray(),
        });

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.Equal(AdvisorResponseParser.MaxQuestions, parsed.Questions.Count);
        Assert.Equal("q0", parsed.Questions[0].Prompt);
        Assert.Equal("q4", parsed.Questions[4].Prompt);
    }

    [Fact]
    public void Parse_TruncatesToSixOptionsPerQuestion()
    {
        var json = """
        {
          "reply": "ok",
          "questions": [
            { "prompt": "p", "options": ["a","b","c","d","e","f","g","h"] }
          ]
        }
        """;

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        var q = Assert.Single(parsed.Questions);
        Assert.Equal(AdvisorResponseParser.MaxOptionsPerQuestion, q.Options.Count);
    }

    [Fact]
    public void Parse_QuestionWithoutPrompt_Throws()
    {
        var json = """{"questions":[{"prompt":"  ","options":["a","b"]}]}""";

        Assert.Throws<AdvisorResponseInvalidException>(() =>
            AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance));
    }

    [Fact]
    public void Parse_QuestionWithoutOptions_Throws()
    {
        var json = """{"questions":[{"prompt":"p","options":[]}]}""";

        Assert.Throws<AdvisorResponseInvalidException>(() =>
            AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance));
    }

    [Fact]
    public void Parse_TruncatesToEightGaps()
    {
        var gaps = Enumerable.Range(0, 10).Select(i => new
        {
            title = $"g{i}",
            detail = $"d{i}",
            band = "Developing",
        }).ToArray();
        var json = JsonSerializer.Serialize(new { reply = "ok", summary = new { gaps } });

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Summary);
        Assert.Equal(AdvisorResponseParser.MaxGaps, parsed.Summary!.Gaps.Count);
    }

    [Fact]
    public void Parse_TruncatesToSixSuggestions()
    {
        var suggestions = Enumerable.Range(0, 9).Select(i => new
        {
            title = $"t{i}",
            reason = $"r{i}",
            nextStep = $"n{i}",
        }).ToArray();
        var json = JsonSerializer.Serialize(new { reply = "ok", summary = new { suggestions } });

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Summary);
        Assert.Equal(AdvisorResponseParser.MaxSuggestions, parsed.Summary!.Suggestions.Count);
    }

    [Fact]
    public void Parse_TruncatesToEightContextNotes()
    {
        var notes = Enumerable.Range(0, 11).Select(i => $"note {i}").ToArray();
        var json = JsonSerializer.Serialize(new { reply = "ok", contextNotes = notes });

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.Equal(AdvisorResponseParser.MaxContextNotes, parsed.ContextNotes.Count);
    }

    [Fact]
    public void Parse_CapsReplyAtSixThousandCharacters()
    {
        var oversized = new string('x', AdvisorResponseParser.MaxReplyCharacters + 100);
        var json = JsonSerializer.Serialize(new { reply = oversized });

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Reply);
        Assert.Equal(AdvisorResponseParser.MaxReplyCharacters, parsed.Reply!.Length);
    }

    [Fact]
    public void Parse_CapsTitleAtTwoHundredCharacters()
    {
        var oversized = new string('t', AdvisorResponseParser.MaxTitleCharacters + 50);
        var json = JsonSerializer.Serialize(new { reply = "ok", title = oversized });

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Title);
        Assert.Equal(AdvisorResponseParser.MaxTitleCharacters, parsed.Title!.Length);
    }

    [Fact]
    public void Parse_CapsContextNoteAtThreeHundredCharacters()
    {
        var oversized = new string('n', AdvisorResponseParser.MaxNoteCharacters + 50);
        var json = JsonSerializer.Serialize(new { reply = "ok", contextNotes = new[] { oversized } });

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        var note = Assert.Single(parsed.ContextNotes);
        Assert.Equal(AdvisorResponseParser.MaxNoteCharacters, note.Length);
    }

    [Fact]
    public void Parse_InvalidBandNormalizesToNull()
    {
        var json = """
        {
          "reply": "ok",
          "summary": {
            "gaps": [
              { "title": "g1", "detail": "d1", "band": "Outstanding" },
              { "title": "g2", "detail": "d2", "band": "Developing" }
            ]
          }
        }
        """;

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Summary);
        Assert.Equal(2, parsed.Summary!.Gaps.Count);
        Assert.Null(parsed.Summary.Gaps[0].Band);
        Assert.Equal("Developing", parsed.Summary.Gaps[1].Band);
    }

    [Fact]
    public void Parse_GapWithEmptyTitleOrDetailIsDropped()
    {
        var json = """
        {
          "reply": "ok",
          "summary": {
            "gaps": [
              { "title": "", "detail": "d" },
              { "title": "t", "detail": "" },
              { "title": "valid", "detail": "ok" }
            ]
          }
        }
        """;

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Summary);
        var gap = Assert.Single(parsed.Summary!.Gaps);
        Assert.Equal("valid", gap.Title);
        Assert.Equal("ok", gap.Detail);
    }

    [Fact]
    public void Parse_SuggestionWithEmptyFieldIsDropped()
    {
        var json = """
        {
          "reply": "ok",
          "summary": {
            "suggestions": [
              { "title": "", "reason": "r", "nextStep": "n" },
              { "title": "t", "reason": "", "nextStep": "n" },
              { "title": "t", "reason": "r", "nextStep": "" },
              { "title": "ok", "reason": "ok", "nextStep": "ok" }
            ]
          }
        }
        """;

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Summary);
        var sug = Assert.Single(parsed.Summary!.Suggestions);
        Assert.Equal("ok", sug.Title);
    }

    [Fact]
    public void Parse_SourceItemIdOutsideCandidateSet_IsDroppedSuggestionKept()
    {
        var id = Guid.NewGuid();
        var json = $$"""
        {
          "reply": "ok",
          "summary": {
            "suggestions": [
              {
                "title": "t1",
                "reason": "r1",
                "nextStep": "n1",
                "sourceItemId": "{{id}}"
              }
            ]
          }
        }
        """;

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Summary);
        var sug = Assert.Single(parsed.Summary!.Suggestions);
        Assert.Equal("t1", sug.Title);
        Assert.Null(sug.SourceFeedItemId);
    }

    [Fact]
    public void Parse_SourceItemIdInsideCandidateSet_RoundTrips()
    {
        var id = Guid.NewGuid();
        var json = $$"""
        {
          "reply": "ok",
          "summary": {
            "suggestions": [
              {
                "title": "t1",
                "reason": "r1",
                "nextStep": "n1",
                "sourceItemId": "{{id}}"
              }
            ]
          }
        }
        """;

        var parsed = AdvisorResponseParser.Parse(
            json,
            new HashSet<Guid> { id },
            NullLogger.Instance);

        Assert.NotNull(parsed.Summary);
        var sug = Assert.Single(parsed.Summary!.Suggestions);
        Assert.Equal(id, sug.SourceFeedItemId);
    }

    [Fact]
    public void Parse_SourceItemIdThatIsNotAGuid_IsDropped()
    {
        var json = """
        {
          "reply": "ok",
          "summary": {
            "suggestions": [
              {
                "title": "t1",
                "reason": "r1",
                "nextStep": "n1",
                "sourceItemId": "not-a-guid"
              }
            ]
          }
        }
        """;

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Summary);
        var sug = Assert.Single(parsed.Summary!.Suggestions);
        Assert.Null(sug.SourceFeedItemId);
    }

    [Fact]
    public void Parse_ChangeNoteCapApplied()
    {
        var oversized = new string('c', AdvisorResponseParser.MaxChangeNoteCharacters + 100);
        var json = JsonSerializer.Serialize(new
        {
            reply = "ok",
            summary = new { changeNote = oversized },
        });

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.NotNull(parsed.Summary);
        Assert.Equal(AdvisorResponseParser.MaxChangeNoteCharacters, parsed.Summary!.ChangeNote!.Length);
    }

    [Fact]
    public void Parse_EmptySummaryIsNull()
    {
        var json = """
        {
          "reply": "ok",
          "summary": { "gaps": [], "suggestions": [], "changeNote": null }
        }
        """;

        var parsed = AdvisorResponseParser.Parse(json, new HashSet<Guid>(), NullLogger.Instance);

        Assert.Null(parsed.Summary);
    }

    [Fact]
    public void Parse_StringAwareJsonExtractor_HandlesFencedJson()
    {
        // Mirrors the real Gemma reasoning model's habit of wrapping JSON
        // in prose and code fences.
        var fenced = """
        Sure, here's the response:

        ```json
        { "reply": "ok", "questions": [ { "prompt": "p", "options": ["a", "b"] } ] }
        ```

        Hope this helps!
        """;

        var parsed = AdvisorResponseParser.Parse(fenced, new HashSet<Guid>(), NullLogger.Instance);

        Assert.Equal("ok", parsed.Reply);
        Assert.Single(parsed.Questions);
    }
}
