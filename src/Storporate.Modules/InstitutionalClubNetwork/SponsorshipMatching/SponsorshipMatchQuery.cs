using System.Text.RegularExpressions;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipMatching;

/// <summary>One cleaned word of a search plus every normalised phrase it stands for (itself and its synonyms).</summary>
public sealed record QueryToken(string Original, IReadOnlyList<string> Terms);

/// <summary>The cleaned plain-word search: kept words with synonyms, and any study years mentioned.</summary>
public sealed record ParsedMatchQuery(IReadOnlyList<QueryToken> Tokens, IReadOnlyList<int> Years)
{
    public static readonly ParsedMatchQuery Empty = new([], []);

    /// <summary>True when nothing usable is left after cleaning, so the search is treated as absent.</summary>
    public bool IsEmpty => Tokens.Count == 0 && Years.Count == 0;
}

/// <summary>What a search found in one candidate.</summary>
public sealed record QueryMatch(IReadOnlyList<string> MatchedWords, IReadOnlyList<int> MatchedYears);

/// <summary>
/// STOR-71 plain-word search. Deliberately simple and deterministic (no language model, no
/// embeddings): lower-case, split on non letters and digits, drop stop words and one-letter tokens,
/// expand each word through a small synonym map, then look for the word or its expansion as a whole
/// word or phrase in the candidate's searchable text. A mention such as "year 2" or "2nd year"
/// becomes a study-year filter.
/// </summary>
public static partial class SponsorshipMatchQuery
{
    public const int MaxLength = 200;
    public const int MaxReportedWords = 3;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "for", "in", "of", "to", "and", "or", "with", "at", "on", "need", "needs",
        "looking", "want", "wants", "students", "student", "club", "company", "event", "events",
        "sponsor", "sponsors", "sponsorship", "who", "that", "from", "by", "near", "year", "years",
    };

    // Word to the fixed-list terms it usually means. Keys and values are folded through SearchText at load.
    private static readonly IReadOnlyDictionary<string, string[]> Synonyms = BuildSynonyms();

    public static ParsedMatchQuery Parse(string? query)
    {
        var lowered = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (lowered.Length == 0)
        {
            return ParsedMatchQuery.Empty;
        }

        var years = new SortedSet<int>();
        foreach (var pattern in new[] { YearBefore(), YearAfter() })
        {
            foreach (Match match in pattern.Matches(lowered))
            {
                years.Add(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }

            lowered = pattern.Replace(lowered, " ");
        }

        var tokens = new List<QueryToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in SearchText.Tokenize(lowered))
        {
            if (word.Length < 2 || StopWords.Contains(word) || !seen.Add(word))
            {
                continue;
            }

            var terms = new List<string> { word };
            if (Synonyms.TryGetValue(SearchText.Normalize(word).Trim(), out var expansion))
            {
                terms.AddRange(expansion);
            }

            tokens.Add(new QueryToken(word, terms));
        }

        return new ParsedMatchQuery(tokens, years.ToList());
    }

    /// <summary>
    /// A candidate matches when at least one word (or a synonym of it) is in its searchable text and,
    /// if the search named study years, the candidate reaches at least one of them.
    /// </summary>
    public static QueryMatch? Match(ParsedMatchQuery query, string searchableText, IReadOnlyCollection<int> candidateYears)
    {
        var matchedYears = query.Years.Where(candidateYears.Contains).ToList();
        if (query.Years.Count > 0 && matchedYears.Count == 0)
        {
            return null;
        }

        var normalized = SearchText.Normalize(searchableText);
        var matchedWords = query.Tokens
            .Where(t => t.Terms.Any(term => SearchText.ContainsPhrase(normalized, term)))
            .Select(t => t.Original)
            .ToList();
        if (query.Tokens.Count > 0 && matchedWords.Count == 0)
        {
            return null;
        }

        return new QueryMatch(matchedWords, matchedYears);
    }

    /// <summary>Plain sentences saying what the search matched, e.g. "Matches your words: hackathon."</summary>
    public static IReadOnlyList<string> ReasonsFor(QueryMatch match)
    {
        var reasons = new List<string>();
        if (match.MatchedWords.Count > 0)
        {
            reasons.Add($"Matches your words: {string.Join(", ", match.MatchedWords.Take(MaxReportedWords))}.");
        }

        if (match.MatchedYears.Count > 0)
        {
            reasons.Add($"Matches the study year you asked for: {SponsorshipMatcher.JoinWords(match.MatchedYears.Select(y => $"Year {y}").ToList())}.");
        }

        return reasons;
    }

    private static Dictionary<string, string[]> BuildSynonyms()
    {
        string[] tech = ["hackathon", "computer science"];
        string[] recruiting = ["recruiting", "career fair"];
        string[] csr = ["csr education", "community outreach"];
        string[] sports = ["sports event"];
        string[] culture = ["cultural event"];
        string[] business = ["business"];

        var raw = new (string[] Words, string[] Terms)[]
        {
            (["coding", "programming", "software", "tech", "technology", "developer", "developers", "code", "computing", "ai", "data"], tech),
            (["recruit", "recruiting", "recruitment", "hiring", "jobs", "job", "careers", "career", "internship", "internships"], recruiting),
            (["csr", "community", "charity", "volunteering", "social"], csr),
            (["sports", "sport", "football", "cricket", "athletics"], sports),
            (["culture", "cultural", "music", "art", "arts", "festival", "drama"], culture),
            (["startup", "startups", "business", "entrepreneurship", "entrepreneur", "commerce", "finance"], business),
            (["brand", "branding", "marketing", "advertising"], ["brand awareness"]),
            (["launch", "launching"], ["product launch"]),
            (["contest", "contests", "championship", "olympiad", "challenge"], ["competition"]),
            (["training", "bootcamp", "masterclass"], ["workshop"]),
            (["talk", "talks", "lecture", "webinar", "panel"], ["seminar"]),
            (["summit", "symposium"], ["conference"]),
            (["cs", "cse"], ["computer science"]),
        };

        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (words, terms) in raw)
        {
            foreach (var word in words)
            {
                map[SearchText.Normalize(word).Trim()] = terms;
            }
        }

        return map;
    }

    [GeneratedRegex(@"\byear\s*([1-6])\b")]
    private static partial Regex YearBefore();

    [GeneratedRegex(@"\b([1-6])\s*(?:st|nd|rd|th)?\s*year\b")]
    private static partial Regex YearAfter();
}
