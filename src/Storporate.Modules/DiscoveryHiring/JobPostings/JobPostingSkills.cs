using System.Text.Encodings.Web;
using System.Text.Json;

namespace Storporate.Modules.DiscoveryHiring.JobPostings;

/// <summary>Helpers for the stored required-skills JSON array.</summary>
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
}
