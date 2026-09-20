using System.Text.Json;
using System.Text.Json.Serialization;
using Storporate.Infrastructure.Llm;

namespace Storporate.Tests.Unit.Llm;

/// <summary>
/// Coverage for <see cref="LlmJsonExtractor"/>. The previous private helper
/// (<c>ExtractJsonArraySlice</c> in <c>PortfolioAnalysisJobProcessor</c>) did
/// not skip brackets inside string values, so a single <c>"]"</c> inside a
/// skill explanation ended the slice early and surfaced as a parse error in
/// production. The new shared extractor must handle that case correctly and
/// also support the object-shaped output the advisor pipeline will return.
/// </summary>
/// <remarks>
/// <para>
/// Each test class member maps to one line of the Step E acceptance criteria
/// in the plan: object and array bodies, leading and trailing prose, brackets
/// inside string values, unbalanced input. The <c>JsonException</c>-throwing
/// <see cref="LlmJsonExtractor.Deserialize{T}(string?, System.Text.Json.JsonSerializerOptions?)"/>
/// overload is exercised alongside the raw slice path so callers get both
/// surfaces verified.
/// </para>
/// </remarks>
public class LlmJsonExtractorTests
{
    /// <summary>Mirrors the project's default JSON options
    /// (<c>JsonSerializerDefaults.Web</c>, camelCase). The advisor pipeline and
    /// the existing portfolio analyzer both serialize / deserialize against
    /// this shape, so the deserialization tests pin it explicitly.</summary>
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
    [Fact]
    public void ExtractJsonDocument_PlainArray_ReturnsArraySlice()
    {
        const string Body = "[{\"skill\":\"React\",\"band\":\"Strong\"}]";

        var slice = LlmJsonExtractor.ExtractJsonDocument(Body);

        Assert.NotNull(slice);
        Assert.Equal(ExtractedJsonDocumentKind.Array, slice!.Kind);
        Assert.Equal(Body, slice.Text);
    }

    [Fact]
    public void ExtractJsonDocument_PlainObject_ReturnsObjectSlice()
    {
        const string Body = "{\"reply\":\"hello\",\"questions\":[]}";

        var slice = LlmJsonExtractor.ExtractJsonDocument(Body);

        Assert.NotNull(slice);
        Assert.Equal(ExtractedJsonDocumentKind.Object, slice!.Kind);
        Assert.Equal(Body, slice.Text);
    }

    [Fact]
    public void ExtractJsonDocument_LeadingAndTrailingProse_StripsAroundSlice()
    {
        const string Body = "Sure, here you go:\n[{\"skill\":\"Go\",\"band\":\"Strong\"}]\nHope that helps!";

        var slice = LlmJsonExtractor.ExtractJsonDocument(Body);

        Assert.NotNull(slice);
        Assert.Equal(ExtractedJsonDocumentKind.Array, slice!.Kind);
        Assert.Equal("[{\"skill\":\"Go\",\"band\":\"Strong\"}]", slice.Text);
    }

    [Fact]
    public void ExtractJsonDocument_ProseAroundObject_StripsAroundSlice()
    {
        const string Body = "Here is the answer you asked for:\n{\"reply\":\"hi\"}\nGood luck.";

        var slice = LlmJsonExtractor.ExtractJsonDocument(Body);

        Assert.NotNull(slice);
        Assert.Equal(ExtractedJsonDocumentKind.Object, slice!.Kind);
        Assert.Equal("{\"reply\":\"hi\"}", slice.Text);
    }

    [Fact]
    public void ExtractJsonDocument_BracketInsideStringValue_DoesNotEndSliceEarly()
    {
        // The known Gemma quirk: a literal ']' inside a string value. The old
        // helper closed the array here and broke parsing.
        const string Body = "[{\"skill\":\"Python list slicing\",\"explanation\":\"uses a[x ] y pattern\"}]";

        var slice = LlmJsonExtractor.ExtractJsonDocument(Body);

        Assert.NotNull(slice);
        Assert.Equal(ExtractedJsonDocumentKind.Array, slice!.Kind);
        Assert.Equal(Body, slice.Text);
    }

    [Fact]
    public void ExtractJsonDocument_OpenBracketInsideString_DoesNotStartSliceAtWrongPosition()
    {
        // The '[' inside the prose must not be picked up as the start of
        // the array — the prose is followed by a real JSON document, and
        // the slice must point at that document, not at the bare bracket.
        const string Body = "prefix with [ in prose\n[{\"skill\":\"x\"}]";

        var slice = LlmJsonExtractor.ExtractJsonDocument(Body);

        Assert.NotNull(slice);
        Assert.Equal(ExtractedJsonDocumentKind.Array, slice!.Kind);
        Assert.Equal("[{\"skill\":\"x\"}]", slice.Text);
    }

    [Fact]
    public void ExtractJsonDocument_NestedObjects_DepthCounterClosesAtCorrectBracket()
    {
        const string Body = "[{\"skill\":\"Go\",\"meta\":{\"level\":\"expert\"}},{\"skill\":\"Rust\"}]";

        var slice = LlmJsonExtractor.ExtractJsonDocument(Body);

        Assert.NotNull(slice);
        Assert.Equal(Body, slice!.Text);
    }

    [Fact]
    public void ExtractJsonDocument_UnbalancedOpeningBracket_ReturnsNull()
    {
        const string Body = "[{\"skill\":\"Go\"";

        var slice = LlmJsonExtractor.ExtractJsonDocument(Body);

        Assert.Null(slice);
    }

    [Fact]
    public void ExtractJsonDocument_NoJsonAtAll_ReturnsNull()
    {
        const string Body = "Sorry, I can't help with that.";

        var slice = LlmJsonExtractor.ExtractJsonDocument(Body);

        Assert.Null(slice);
    }

    [Fact]
    public void ExtractJsonDocument_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(LlmJsonExtractor.ExtractJsonDocument(null));
        Assert.Null(LlmJsonExtractor.ExtractJsonDocument(string.Empty));
        Assert.Null(LlmJsonExtractor.ExtractJsonDocument("   "));
    }

    [Fact]
    public void Deserialize_ObjectPayload_RoundTripsToTargetType()
    {
        const string Body = "{\"name\":\"Go\",\"level\":3}";

        var result = LlmJsonExtractor.Deserialize<SamplePayload>(Body, WebOptions);

        Assert.Equal("Go", result.Name);
        Assert.Equal(3, result.Level);
    }

    [Fact]
    public void Deserialize_ArrayPayloadWrappedInProse_RoundTripsToList()
    {
        const string Body = "Here are the skills:\n[{\"skill\":\"Go\"},{\"skill\":\"Rust\"}]\n";

        var result = LlmJsonExtractor.Deserialize<List<SampleSkill>>(Body, WebOptions);

        Assert.Equal(2, result.Count);
        Assert.Equal("Go", result[0].Skill);
        Assert.Equal("Rust", result[1].Skill);
    }

    [Fact]
    public void Deserialize_NoJson_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(
            () => LlmJsonExtractor.Deserialize<SamplePayload>("no json here", WebOptions));
    }

    private sealed class SamplePayload
    {
        public string? Name { get; init; }
        public int Level { get; init; }
    }

    private sealed class SampleSkill
    {
        public string? Skill { get; init; }
    }
}