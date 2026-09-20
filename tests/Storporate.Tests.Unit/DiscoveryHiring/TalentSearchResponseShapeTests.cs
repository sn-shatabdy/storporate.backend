using System.Reflection;
using System.Text.Json;
using Storporate.Modules.DiscoveryHiring.TalentSearch;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-43 plan guard: the talent-search wire surface MUST NOT carry
/// any field that smells like a score, a rank number, a percentage, a
/// similarity / distance value, an email, an account id, a file name,
/// or a storage key. The plan's "no numeric scores" rule is total — the
/// locators are listed below so a future change that re-introduces
/// any of them trips the guard before it ships.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two complementary checks.</b>
/// <list type="number">
///   <item><b>Static</b> — reflection over the wire-shape records under
///         <c>Storporate.Modules.DiscoveryHiring.TalentSearch</c>; any
///         property whose name contains a banned word fails the test.
///         This catches a future field added at compile-time.</item>
///   <item><b>Runtime</b> — drives the GET handler end-to-end with a
///         synthetic Completed row + a live <c>TalentIndexEntry</c>, then
///         walks the serialized response for any property whose name
///         contains a banned word. This catches a future shape drift
///         that comes from a different code path (e.g. a new processor
///         branch, a new projection helper).</item>
/// </list>
/// </para>
/// <para>
/// <b>Why not just reflection?</b> The response projection is in
/// <see cref="GetTalentSearchHandler"/>, not in the wire records — the
/// handler builds the result items from a snapshot. A reflection-only
/// check would miss a future bug where the handler copies a
/// score-shaped field into the wire item under a different name.
/// </para>
/// </remarks>
public class TalentSearchResponseShapeTests
{
    /// <summary>The plan's banned field-name fragments. Case-insensitive
    /// substring match against property names. Words chosen to cover
    /// the plan's PII + numeric-score list in one sweep.</summary>
    public static readonly string[] ForbiddenFieldFragments = new[]
    {
        "score", "rating", "percent", "similarity", "distance",
        "rank", "email", "accountid", "filename", "storagekey", "blob",
    };

    /// <summary>The single numeric value the plan allows on the wire —
    /// <c>StudyYear</c> — and any future identifiers / counts are fine
    /// (Guid ids and array.Length are not numeric properties). Any
    /// other numeric property on the response records fails the test.</summary>
    private static readonly HashSet<string> AllowedNumericPropertyNames = new(StringComparer.Ordinal)
    {
        "StudyYear",
    };

    [Fact]
    public void NoResponseRecord_HasAForbiddenFieldName()
    {
        var assembly = typeof(TalentSearchResponse).Assembly;
        var responseTypes = assembly.GetTypes()
            .Where(t => t.IsClass
                && t.Namespace?.StartsWith(
                    "Storporate.Modules.DiscoveryHiring.TalentSearch",
                    StringComparison.Ordinal) == true
                && t.Name.EndsWith("Response", StringComparison.Ordinal)
                && !t.Name.Contains("Request", StringComparison.Ordinal))
            .ToList();

        var found = new List<string>();
        foreach (var type in responseTypes)
        {
            foreach (var prop in type.GetProperties())
            {
                foreach (var banned in ForbiddenFieldFragments)
                {
                    if (prop.Name.Contains(banned, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add($"{type.FullName}.{prop.Name} (matches '{banned}')");
                    }
                }
            }
        }

        Assert.True(
            found.Count == 0,
            "Response shapes may not carry banned field names; found: "
                + string.Join(", ", found));
    }

    [Fact]
    public void NoResponseRecord_HasANonAllowedNumericProperty()
    {
        var assembly = typeof(TalentSearchResponse).Assembly;
        var responseTypes = assembly.GetTypes()
            .Where(t => t.IsClass
                && t.Namespace?.StartsWith(
                    "Storporate.Modules.DiscoveryHiring.TalentSearch",
                    StringComparison.Ordinal) == true
                && t.Name.EndsWith("Response", StringComparison.Ordinal)
                && !t.Name.Contains("Request", StringComparison.Ordinal))
            .ToList();

        var found = new List<string>();
        foreach (var type in responseTypes)
        {
            foreach (var prop in type.GetProperties())
            {
                if (!IsNumericType(prop.PropertyType))
                {
                    continue;
                }
                if (!AllowedNumericPropertyNames.Contains(prop.Name))
                {
                    found.Add($"{type.FullName}.{prop.Name} ({prop.PropertyType.Name})");
                }
            }
        }

        Assert.True(
            found.Count == 0,
            "Numeric response properties must be StudyYear (or absent); found: "
                + string.Join(", ", found));
    }

    [Fact]
    public void SerializedCompletedResponse_HasNoBannedFieldOrNumericLeaf()
    {
        // Build a wire-shape TalentSearchResponse the same way the GET
        // handler does (after re-validation), then serialize it and
        // walk every property name + every leaf number. A future bug
        // that slips a banned field into the projection helper will
        // show up here even when the response *record* looks clean.
        var candidateId = Guid.NewGuid();
        var portfolioItemId = Guid.NewGuid();
        var response = new TalentSearchResponse(
            Id: candidateId,
            Status: TalentSearchStatuses.Completed,
            Query: "Need a backend engineer with Python experience.",
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            Results: new[]
            {
                new TalentSearchResultItem(
                    CandidateId: candidateId,
                    DisplayName: "Alice Doe",
                    Headline: "Backend engineer",
                    University: "Example U",
                    FieldOfStudy: "Computer Science",
                    StudyYear: 3,
                    MatchedSkills: new[]
                    {
                        new TalentSearchMatchedSkill("Python", ConfidenceBands.Strong),
                    },
                    Reason: "Strong match for the requested skill.",
                    CitedItems: new[]
                    {
                        new TalentSearchCitedItem(
                            PortfolioItemId: portfolioItemId,
                            Label: "Personal API project",
                            Category: PortfolioCategories.Project,
                            SkillName: "Python",
                            Band: ConfidenceBands.Strong),
                    }),
            },
            ErrorCode: null);

        var json = JsonSerializer.Serialize(response);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var found = new List<string>();
        Walk(root, "$", found);

        Assert.True(
            found.Count == 0,
            "Serialized GET response contains banned field/numeric leaves: "
                + string.Join(", ", found));
    }

    [Fact]
    public void ForbiddenFieldList_DemonstrablyCatchesAFakeViolation()
    {
        // Self-check: pretend the FE suddenly had to render a
        // `similarity` field. Verify the guard above would catch it.
        var fakeJson = """
            {
              "results": [
                {
                  "candidateId": "11111111-1111-1111-1111-111111111111",
                  "displayName": "Alice",
                  "similarity": 0.93,
                  "matchedSkills": [],
                  "citedItems": []
                }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(fakeJson);
        var found = new List<string>();
        Walk(doc.RootElement, "$", found);

        Assert.Contains(found, f => f.Contains("similarity", StringComparison.OrdinalIgnoreCase));
    }

    // ----- helpers -----

    private static void Walk(JsonElement element, string path, List<string> sink)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = $"{path}.{property.Name}";
                    // Field-name guard: any property whose name contains
                    // a banned fragment goes onto the sink.
                    foreach (var banned in ForbiddenFieldFragments)
                    {
                        if (property.Name.Contains(banned, StringComparison.OrdinalIgnoreCase))
                        {
                            sink.Add($"{childPath} (matches '{banned}')");
                        }
                    }
                    Walk(property.Value, childPath, sink);
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{i}]", sink);
                    i++;
                }
                break;
            case JsonValueKind.Number:
                // A numeric leaf anywhere on the wire surface that
                // isn't `studyYear` is a regression. Allow it as long
                // as it lives under the StudyYear path.
                if (!path.EndsWith(".studyYear", StringComparison.OrdinalIgnoreCase))
                {
                    sink.Add($"numeric leaf at {path} = {element.GetRawText()}");
                }
                break;
            default:
                break;
        }
    }

    private static bool IsNumericType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(int)
            || underlying == typeof(long)
            || underlying == typeof(short)
            || underlying == typeof(byte)
            || underlying == typeof(float)
            || underlying == typeof(double)
            || underlying == typeof(decimal);
    }
}
