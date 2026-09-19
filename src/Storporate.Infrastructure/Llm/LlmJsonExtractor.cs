using System.Text.Json;

namespace Storporate.Infrastructure.Llm;

/// <summary>
/// String-aware JSON slicer for LLM output. Local reasoning models routinely
/// wrap their JSON in stray prose ("Here is the JSON you asked for: {...}"),
/// insert fence markers, or — most perniciously — emit a literal
/// <c>"x ] y"</c> inside a string value. <see cref="System.Text.Json.JsonSerializer"/>
/// is unforgiving about all three; this extractor returns the smallest JSON
/// document (object or array) it can find inside an arbitrary string, or
/// <see langword="null"/> when none is present.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why string-aware.</b> The previous private helper
/// (<c>ExtractJsonArraySlice</c> in <c>PortfolioAnalysisJobProcessor</c>) walked
/// the buffer counting brackets but ignored quoted strings. A single
/// <c>"]"</c> inside a string ended the slice early, the resulting substring
/// was no longer valid JSON, and the worker surfaced it as a parse error —
/// visible to the user as "an unexpected retry". The local Gemma reasoning
/// model is particularly prone to this; the new advisor pipeline will read
/// far longer outputs, so the helper has to be right.
/// </para>
/// <para>
/// <b>Why two return types.</b> The portfolio analyzer returns an array;
/// the advisor returns an object. The extractor therefore reports
/// <see cref="ExtractedJsonDocumentKind"/> alongside the slice so callers
/// deserialize with the matching shape.
/// </para>
/// <para>
/// <b>Why public-static.</b> Both <c>PortfolioAnalysisJobProcessor</c> and
/// the forthcoming <c>AdvisorTurnProcessor</c> need it; an instance type
/// would just push the same code into two places.
/// </para>
/// </remarks>
public static class LlmJsonExtractor
{
    /// <summary>
    /// Extract the first well-formed JSON document (object or array) from
    /// <paramref name="output"/>, skipping any leading or trailing prose and
    /// correctly handling brackets that appear inside string values.
    /// </summary>
    /// <param name="output">The raw LLM response body.</param>
    /// <returns>
    /// A <see cref="JsonSlice"/> describing the slice and its
    /// <see cref="ExtractedJsonDocumentKind"/>, or <see langword="null"/> if
    /// no balanced JSON document is found. An unbalanced starting bracket
    /// also returns <see langword="null"/> rather than a partial slice —
    /// callers should retry the LLM rather than guess at the missing tail.
    /// </returns>
    public static JsonSlice? ExtractJsonDocument(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        // Scan once looking for the first '[' or '{' that begins a real
        // JSON document (not a stray bracket inside prose or inside a string
        // literal). A local-model quirk we have to defend against is a
        // literal '[' or '{' inside prose (or inside a string) that
        // precedes the real JSON document — the previous private helper
        // took that earlier bracket as the slice start and broke parsing.
        // We therefore check for the opening quote FIRST on every character
        // so any string literal — and the brackets inside it — are skipped
        // past before we ever consider a bracket as the document start.
        // A bare '[' / '{' that is followed by prose rather than a JSON
        // value is also discarded: we look for whitespace and then a value
        // starter ('"', digit, '-', 't', 'f', 'n', '[', '{') before
        // accepting the bracket.
        var start = -1;
        var openChar = '\0';
        for (var i = 0; i < output.Length; i++)
        {
            var c = output[i];

            if (c == '"')
            {
                i = SkipQuotedString(output, i) - 1;
                continue;
            }

            if (c == '[' || c == '{')
            {
                if (LooksLikeJsonDocumentStart(output, i))
                {
                    start = i;
                    openChar = c;
                    break;
                }

                // Not a JSON document start — keep scanning so the real
                // document (if any) further down the buffer is found.
                continue;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var closeChar = openChar == '[' ? ']' : '}';
        var end = FindBalancedClose(output, start, openChar, closeChar);
        if (end < 0)
        {
            // Unbalanced — refuse to return a partial slice. The retry/fail
            // path will surface the bad output to the user.
            return null;
        }

        var kind = openChar == '['
            ? ExtractedJsonDocumentKind.Array
            : ExtractedJsonDocumentKind.Object;

        return new JsonSlice(output.Substring(start, end - start + 1), kind);
    }

    /// <summary>
    /// Walk forward from <paramref name="startIndex"/> counting nested
    /// <paramref name="openChar"/>s and matching <paramref name="closeChar"/>s
    /// while ignoring any that appear inside a quoted string. Returns the
    /// index of the matching close, or <c>-1</c> if the input is unbalanced.
    /// </summary>
    private static int FindBalancedClose(string output, int startIndex, char openChar, char closeChar)
    {
        var depth = 0;
        for (var i = startIndex; i < output.Length; i++)
        {
            var c = output[i];

            if (c == '"')
            {
                i = SkipQuotedString(output, i) - 1;
                continue;
            }

            if (c == openChar)
            {
                depth++;
            }
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
            // Any other character (whitespace, ':' , ',' , digit, letter) is
            // ignored; we are only counting structural brackets.
        }

        return -1;
    }

    /// <summary>
    /// Given the index of a candidate <c>[</c> or <c>{</c> opener, decide
    /// whether it really begins a JSON document. A bare bracket followed by
    /// prose (e.g. a markdown bullet that opens with <c>[</c>) is not a JSON
    /// start — the real document, if any, is further down the buffer. We
    /// require the next non-whitespace character to be one of the JSON
    /// value-start characters: <c>"</c>, digit, <c>-</c>, <c>t</c> (true),
    /// <c>f</c> (false), <c>n</c> (null), or another opener.
    /// </summary>
    private static bool LooksLikeJsonDocumentStart(string output, int openerIndex)
    {
        for (var i = openerIndex + 1; i < output.Length; i++)
        {
            var c = output[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            return c == '"' || c == '[' || c == '{'
                || (c >= '0' && c <= '9')
                || c == '-'
                || c == 't' || c == 'f' || c == 'n';
        }

        return false;
    }

    /// <summary>
    /// Given the index of an opening <c>"</c>, advance past the matching
    /// closing <c>"</c> per JSON string rules (the backslash escapes any
    /// following character, including another backslash). Returns the index
    /// of the character AFTER the closing quote.
    /// </summary>
    private static int SkipQuotedString(string output, int openQuoteIndex)
    {
        for (var i = openQuoteIndex + 1; i < output.Length; i++)
        {
            var c = output[i];
            if (c == '\\')
            {
                // Skip the escaped character (may itself be a quote, a
                // bracket, another backslash, etc.). Don't try to validate
                // the escape sequence — System.Text.Json will, and an
                // invalid sequence just means we hand off to it earlier
                // than we'd like.
                i++;
                continue;
            }

            if (c == '"')
            {
                return i + 1;
            }
        }

        // Unterminated string — return past-the-end so the caller keeps
        // scanning rather than spinning inside the quote forever.
        return output.Length;
    }

    /// <summary>
    /// Convenience: deserialize the JSON document directly into
    /// <typeparamref name="T"/>, throwing <see cref="JsonException"/> if no
    /// balanced document can be found or it cannot be deserialized.
    /// </summary>
    public static T Deserialize<T>(string? output, JsonSerializerOptions? options = null)
    {
        var slice = ExtractJsonDocument(output)
            ?? throw new JsonException("LLM output contained no JSON object or array.");

        return JsonSerializer.Deserialize<T>(slice.Text, options)
            ?? throw new JsonException("LLM JSON deserialized to null.");
    }
}

/// <summary>The shape of the JSON document returned by
/// <see cref="LlmJsonExtractor.ExtractJsonDocument"/>.</summary>
/// <param name="Text">The exact substring of the LLM output that contains the
/// balanced JSON document (including the opening and closing braces/brackets).</param>
/// <param name="Kind">Whether the slice is an array or an object, so the
/// caller can deserialize with the matching shape.</param>
public sealed record JsonSlice(string Text, ExtractedJsonDocumentKind Kind);

/// <summary>Discriminator for <see cref="JsonSlice"/>.</summary>
public enum ExtractedJsonDocumentKind
{
    /// <summary>The slice begins with <c>[</c> and ends with the matching
    /// <c>]</c>.</summary>
    Array,

    /// <summary>The slice begins with <c>{</c> and ends with the matching
    /// <c>}</c>.</summary>
    Object,
}