using System.Text.Encodings.Web;
using System.Text.Json;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;

/// <summary>The fixed vocabularies of a sponsorship goal set, plus the JSON and normalisation helpers.</summary>
/// <remarks>Budget amounts are in BDT (Bangladeshi taka); there is deliberately no currency column.</remarks>
public static class SponsorshipGoalOptions
{
    public static readonly IReadOnlyList<string> Objectives =
    [
        "Brand awareness",
        "Recruiting",
        "CSR education",
        "Community outreach",
        "Product launch",
        "Other",
    ];

    public static readonly IReadOnlyList<string> EventKinds =
    [
        "Hackathon",
        "Competition",
        "Workshop",
        "Career fair",
        "Conference",
        "Cultural event",
        "Sports event",
        "Seminar",
    ];

    // Relaxed escaping keeps non-ASCII names readable in the stored text so the club filters
    // can match them with a plain case-insensitive contains.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Trim, drop blanks, de-duplicate case-insensitively (first spelling wins).</summary>
    public static List<string> NormalizeNames(IEnumerable<string?> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var trimmed = raw?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    public static List<int> NormalizeYears(IEnumerable<int> years) => years.Distinct().Order().ToList();

    /// <summary>Maps each entry to its canonical spelling in <paramref name="vocabulary"/> (case-insensitive), de-duplicated in request order. Entries outside the list are dropped.</summary>
    public static List<string> NormalizeFromList(IEnumerable<string?> entries, IReadOnlyList<string> vocabulary)
    {
        var result = new List<string>();
        foreach (var raw in entries)
        {
            var match = vocabulary.FirstOrDefault(v => string.Equals(v, raw?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null && !result.Contains(match, StringComparer.Ordinal))
            {
                result.Add(match);
            }
        }

        return result;
    }

    public static bool IsInList(string? entry, IReadOnlyList<string> vocabulary) =>
        entry is not null && vocabulary.Any(v => string.Equals(v, entry.Trim(), StringComparison.OrdinalIgnoreCase));

    public static T Deserialize<T>(string json, T fallback)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
