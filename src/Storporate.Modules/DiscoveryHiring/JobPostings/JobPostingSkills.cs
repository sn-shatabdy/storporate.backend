using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Storporate.Modules.DiscoveryHiring.JobPostings;

/// <summary>Helpers for the stored required-skills JSON array and the searchable text.</summary>
public static class JobPostingSkills
{
    // Relaxed escaping keeps non-ASCII skill names readable in the stored text so the
    // browse search can match them with a plain case-insensitive contains.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Trim, drop blanks, de-duplicate case-insensitively (first spelling wins).</summary>
    public static List<string> Normalize(IEnumerable<string> skills)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in skills)
        {
            var trimmed = raw?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    public static string Serialize(IReadOnlyList<string> skills) => JsonSerializer.Serialize(skills, Options);

    public static IReadOnlyList<string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Lower-case searchable text: title + company name + location + the normalised skills
    /// joined by single spaces. All brackets / quotes / commas that the JSON serialization
    /// would add are stripped so a query word never matches punctuation. Used by the SQL
    /// backfill in the <c>JobPostingRedo</c> migration and by the create / update handlers.
    /// </summary>
    public static string BuildSearchText(
        string title,
        string companyName,
        string? location,
        IReadOnlyList<string> requiredSkills)
    {
        var builder = new StringBuilder();
        AppendWord(builder, title);
        AppendWord(builder, companyName);
        if (!string.IsNullOrWhiteSpace(location))
        {
            AppendWord(builder, location);
        }

        foreach (var skill in requiredSkills)
        {
            AppendWord(builder, skill);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Lower-case + collapse whitespace for a single word. Strips non-letter / non-digit
    /// characters that the JSON serializer would inject between adjacent words.
    /// </summary>
    private static void AppendWord(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = Regex.Replace(value, @"[^\p{L}\p{Nd}]+", " ").Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(normalized.ToLowerInvariant());
    }
}
