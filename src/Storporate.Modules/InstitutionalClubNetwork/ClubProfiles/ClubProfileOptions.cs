using System.Text.Encodings.Web;
using System.Text.Json;

namespace Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;

/// <summary>The fixed vocabularies of the club profile: event frequency and support needs.</summary>
public static class ClubProfileOptions
{
    public const string OneOff = "OneOff";
    public const string Monthly = "Monthly";
    public const string Termly = "Termly";
    public const string Yearly = "Yearly";

    public static readonly IReadOnlySet<string> Frequencies = new HashSet<string>(StringComparer.Ordinal)
    {
        OneOff,
        Monthly,
        Termly,
        Yearly,
    };

    public static readonly IReadOnlyList<string> SupportNeeds =
    [
        "Funding",
        "Venue",
        "Food and drink",
        "Prizes",
        "Speakers",
        "Equipment",
        "Promotion",
        "Volunteers",
    ];

    // Relaxed escaping keeps non-ASCII names readable in the stored text so the browse filters
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
}
