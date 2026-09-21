using System.Text;
using Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipMatching;

/// <summary>Fit of one club and one goal set, as a word. Never a number.</summary>
public enum FitBand
{
    Strong,
    Good,
    Partial,
}

/// <summary>One event of a club, reduced to the text the matcher reads. Events carry no structured kind.</summary>
public sealed record MatchEvent(string Title, string? Description);

/// <summary>The club side of a match, reduced to what the pure matcher needs.</summary>
public sealed record MatchClub(
    string Name,
    string University,
    string? City,
    IReadOnlyList<string> Fields,
    IReadOnlyList<int> Years,
    IReadOnlyList<MatchEvent> Events);

/// <summary>The company side of a match (one goal set), reduced to what the pure matcher needs.</summary>
public sealed record MatchGoal(
    string Name,
    string CompanyName,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> EventKinds,
    IReadOnlyList<string> Fields,
    IReadOnlyList<int> Years,
    IReadOnlyList<string> Cities,
    IReadOnlyList<string> Universities);

/// <summary>The real overlaps between one goal set and one club, spelled as the goal set spells them.</summary>
public sealed record MatchOverlap(
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> EventKinds,
    IReadOnlyList<string> Universities,
    IReadOnlyList<int> Years,
    IReadOnlyList<string> Cities)
{
    public bool HasFields => Fields.Count > 0;

    public bool HasEventKinds => EventKinds.Count > 0;

    public bool HasUniversities => Universities.Count > 0;

    public bool HasYears => Years.Count > 0;

    public bool HasCities => Cities.Count > 0;

    /// <summary>How many of the five dimensions overlap.</summary>
    public int DimensionCount =>
        (HasFields ? 1 : 0) + (HasEventKinds ? 1 : 0) + (HasUniversities ? 1 : 0) + (HasYears ? 1 : 0) + (HasCities ? 1 : 0);
}

public enum MatchPerspective
{
    /// <summary>A company reading a club ("They run hackathons...").</summary>
    Company,

    /// <summary>A club reading a company ("They back hackathons...").</summary>
    Club,
}

/// <summary>Band, internal ordering points and reasons of one match. Points are for ordering only and never leave the server.</summary>
public sealed record MatchAssessment(FitBand? Band, int Points, IReadOnlyList<string> Reasons);

/// <summary>
/// STOR-71 pure, deterministic matching rules between a sponsorship goal set and a club. Five
/// dimensions are compared case-insensitively after trimming: field of study, event kind, university,
/// study year and city. Event kind is matched against event title and description text, because
/// club events have no structured kind. Fit is a word (<see cref="FitBand"/>); the internal points
/// only break ties inside a band.
/// </summary>
public static class SponsorshipMatcher
{
    public const int MaxReasons = 4;

    private const int FieldPoints = 3;
    private const int EventKindPoints = 3;
    private const int YearPoints = 2;
    private const int UniversityPoints = 2;
    private const int CityPoints = 1;

    // Words that signal an event kind in an event title or description. Compared on normalised text.
    private static readonly IReadOnlyDictionary<string, string[]> KindKeywords =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Hackathon"] = ["hackathon", "hack day", "codefest", "code jam", "game jam"],
            ["Competition"] = ["competition", "contest", "championship", "olympiad", "challenge", "case cup"],
            ["Workshop"] = ["workshop", "bootcamp", "training", "masterclass", "hands on session"],
            ["Career fair"] = ["career fair", "job fair", "career day", "career expo", "internship fair", "recruitment drive"],
            ["Conference"] = ["conference", "summit", "symposium", "convention"],
            ["Cultural event"] = ["cultural", "culture", "concert", "music", "festival", "drama", "art exhibition", "fest"],
            ["Sports event"] = ["sports", "sport", "football", "cricket", "tournament", "marathon", "athletics"],
            ["Seminar"] = ["seminar", "webinar", "lecture", "talk", "panel", "speaker session"],
        };

    public static MatchOverlap ComputeOverlap(MatchGoal goal, MatchClub club)
    {
        var clubFields = new HashSet<string>(club.Fields.Select(Clean), StringComparer.OrdinalIgnoreCase);
        var fields = Distinct(goal.Fields.Where(f => clubFields.Contains(Clean(f))));

        var clubYears = new HashSet<int>(club.Years);
        var years = goal.Years.Where(clubYears.Contains).Distinct().Order().ToList();

        var universities = Distinct(goal.Universities.Where(u => string.Equals(Clean(u), Clean(club.University), StringComparison.OrdinalIgnoreCase)));

        var cities = string.IsNullOrWhiteSpace(club.City)
            ? new List<string>()
            : Distinct(goal.Cities.Where(c => string.Equals(Clean(c), Clean(club.City), StringComparison.OrdinalIgnoreCase)));

        var eventText = club.Events.Select(e => SearchText.Normalize($"{e.Title} {e.Description}")).ToList();
        var eventKinds = Distinct(goal.EventKinds.Where(kind => eventText.Any(text => TextSuggestsKind(text, kind))));

        return new MatchOverlap(fields, eventKinds, universities, years, cities);
    }

    /// <summary>
    /// Strong: event kind overlaps AND (field OR year overlaps) AND at least 3 dimensions overlap.
    /// Good: at least 2 dimensions overlap and one of them is field of study or event kind.
    /// Partial: any other overlap (exactly 1 dimension, or 2 or more that include neither field nor event kind).
    /// No overlap gives no band (<c>null</c>).
    /// </summary>
    public static FitBand? BandOf(MatchOverlap overlap)
    {
        var count = overlap.DimensionCount;
        if (count == 0)
        {
            return null;
        }

        if (overlap.HasEventKinds && (overlap.HasFields || overlap.HasYears) && count >= 3)
        {
            return FitBand.Strong;
        }

        if (count >= 2 && (overlap.HasFields || overlap.HasEventKinds))
        {
            return FitBand.Good;
        }

        return FitBand.Partial;
    }

    /// <summary>Internal ordering weight only. Never returned by any endpoint.</summary>
    public static int PointsOf(MatchOverlap overlap) =>
        (overlap.HasFields ? FieldPoints : 0)
        + (overlap.HasEventKinds ? EventKindPoints : 0)
        + (overlap.HasYears ? YearPoints : 0)
        + (overlap.HasUniversities ? UniversityPoints : 0)
        + (overlap.HasCities ? CityPoints : 0);

    public static MatchAssessment Assess(MatchGoal goal, MatchClub club, MatchPerspective perspective)
    {
        var overlap = ComputeOverlap(goal, club);
        return new MatchAssessment(BandOf(overlap), PointsOf(overlap), ReasonsFor(overlap, perspective));
    }

    /// <summary>One short plain sentence per overlapping dimension, most important first, at most <see cref="MaxReasons"/>.</summary>
    public static IReadOnlyList<string> ReasonsFor(MatchOverlap overlap, MatchPerspective perspective)
    {
        var company = perspective == MatchPerspective.Company;
        var reasons = new List<string>();

        if (overlap.HasFields)
        {
            var fields = JoinWords(overlap.Fields);
            reasons.Add(company
                ? $"Their audience includes {fields}, which you want to reach."
                : $"They want to reach {fields} students, and your audience includes them.");
        }

        if (overlap.HasEventKinds)
        {
            var kinds = JoinWords(overlap.EventKinds.Select(k => Pluralize(k.Trim().ToLowerInvariant())).ToList());
            var single = overlap.EventKinds.Count == 1;
            reasons.Add(company
                ? $"They run {kinds}, {(single ? "an event kind" : "event kinds")} you would back."
                : $"They back {kinds}, and you run {(single ? "one" : "them")}.");
        }

        if (overlap.HasUniversities)
        {
            var universities = JoinWords(overlap.Universities);
            reasons.Add(company
                ? $"They study at {universities}, one of your target universities."
                : $"They want to reach students at {universities}, where your club is based.");
        }

        if (overlap.HasYears)
        {
            var years = JoinWords(overlap.Years.Select(y => $"Year {y}").ToList());
            reasons.Add(company
                ? $"They reach {years} students."
                : $"They want to reach {years} students, and your audience includes them.");
        }

        if (overlap.HasCities)
        {
            var cities = JoinWords(overlap.Cities);
            reasons.Add(company
                ? $"They are based in {cities}, one of your target cities."
                : $"They want to reach students in {cities}, where your club is based.");
        }

        return reasons.Take(MaxReasons).ToList();
    }

    /// <summary>The canonical kinds (from the fixed vocabulary) that at least one of the club's events suggests.</summary>
    public static IReadOnlyList<string> KindsRunBy(MatchClub club)
    {
        var eventText = club.Events.Select(e => SearchText.Normalize($"{e.Title} {e.Description}")).ToList();
        return SponsorshipGoalOptions.EventKinds.Where(kind => eventText.Any(text => TextSuggestsKind(text, kind))).ToList();
    }

    private static bool TextSuggestsKind(string normalizedText, string kind)
    {
        var keywords = KindKeywords.TryGetValue(kind.Trim(), out var known) ? known : [kind];
        return keywords.Any(k => SearchText.ContainsPhrase(normalizedText, k));
    }

    private static string Clean(string? value) => (value ?? string.Empty).Trim();

    private static List<string> Distinct(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values.Select(Clean).Where(v => v.Length > 0 && seen.Add(v)).ToList();
    }

    private static string Pluralize(string word) => word.EndsWith('s') ? word : word + "s";

    /// <summary>"A", "A and B", "A, B and C".</summary>
    internal static string JoinWords(IReadOnlyList<string> words) => words.Count switch
    {
        0 => string.Empty,
        1 => words[0],
        _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1],
    };
}

/// <summary>Shared text normalisation for the matcher and the plain-word search: lower case, letters and digits only, light plural folding.</summary>
public static class SearchText
{
    /// <summary>Space-padded, single-spaced, lower-cased tokens so a phrase can be found with a plain contains.</summary>
    public static string Normalize(string? text)
    {
        var builder = new StringBuilder(" ");
        foreach (var token in Tokenize(text))
        {
            builder.Append(Fold(token)).Append(' ');
        }

        return builder.ToString();
    }

    public static bool ContainsPhrase(string normalizedText, string phrase)
    {
        var normalizedPhrase = Normalize(phrase);
        return normalizedPhrase.Trim().Length > 0 && normalizedText.Contains(normalizedPhrase, StringComparison.Ordinal);
    }

    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string Fold(string token) =>
        token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal)
            ? token[..^1]
            : token;
}
