using System.Reflection;
using Storporate.Modules.StudentGrowthExperience.Responses;

namespace Storporate.Tests.Unit.StudentGrowthExperience;

/// <summary>
/// Reflection-based coverage for the Phase 2 response shapes. The plan's
/// "no numeric scores" rule extends to the wire surface — no
/// <c>Score</c>, <c>Rating</c>, <c>Percent</c>, or <c>Points</c> property
/// anywhere in the advisor responses. Every numeric field has to be an
/// identifier (VersionNumber / LatestVersionNumber) or a count of items
/// (already covered by Array.Length in JSON).
/// </summary>
public class AdvisorResponseShapeTests
{
    private static readonly string[] ForbiddenNumericWords = new[]
    {
        "score", "rating", "percent", "points",
    };

    [Fact]
    public void NoAdvisorResponseRecord_HasAForbiddenNumericProperty()
    {
        var assembly = typeof(ExplorationDetailResponse).Assembly;
        var responseTypes = assembly.GetTypes()
            .Where(t => t.IsClass
                && t.Namespace?.StartsWith(
                    "Storporate.Modules.StudentGrowthExperience.Responses",
                    StringComparison.Ordinal) == true);

        var found = new List<string>();
        foreach (var type in responseTypes)
        {
            foreach (var prop in type.GetProperties())
            {
                foreach (var banned in ForbiddenNumericWords)
                {
                    if (prop.Name.Contains(banned, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add($"{type.FullName}.{prop.Name}");
                    }
                }
            }
        }

        Assert.True(
            found.Count == 0,
            "Response shapes may not carry numeric-score fields; found: "
                + string.Join(", ", found));
    }

    [Fact]
    public void AdvisorResponses_OnlyAcceptableNumericProperty_IsVersionNumber()
    {
        // Every numeric property must be one of { VersionNumber,
        // LatestVersionNumber }. Anything else is a regression — a future
        // score, percentage, or bandwidth-of-confidence field.
        var assembly = typeof(ExplorationDetailResponse).Assembly;
        var responseTypes = assembly.GetTypes()
            .Where(t => t.IsClass
                && t.Namespace?.StartsWith(
                    "Storporate.Modules.StudentGrowthExperience.Responses",
                    StringComparison.Ordinal) == true)
            .ToList();

        var allowedNumericPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "VersionNumber", "LatestVersionNumber",
        };

        var foundNonAllowed = new List<string>();
        foreach (var type in responseTypes)
        {
            foreach (var prop in type.GetProperties())
            {
                var propertyType = prop.PropertyType;
                if (!IsNumericType(propertyType))
                {
                    continue;
                }
                if (!allowedNumericPropertyNames.Contains(prop.Name))
                {
                    foundNonAllowed.Add($"{type.FullName}.{prop.Name} ({propertyType.Name})");
                }
            }
        }

        Assert.True(
            foundNonAllowed.Count == 0,
            "Numeric response properties must be VersionNumber or LatestVersionNumber; found: "
                + string.Join(", ", foundNonAllowed));
    }

    [Fact]
    public void BandValuesOnTheWireAreEitherDevelopingMissingOrNull()
    {
        // Reflects on ExplorationGapResponse: the only allowed string band
        // values are Developing / Missing / null. Anything else is a
        // hard-coded category creep.
        var assembly = typeof(ExplorationDetailResponse).Assembly;
        var gapType = assembly.GetType(
            "Storporate.Modules.StudentGrowthExperience.Responses.ExplorationGapResponse");

        Assert.NotNull(gapType);
        var bandProp = gapType!.GetProperty("Band");
        Assert.NotNull(bandProp);
        Assert.True(
            bandProp!.PropertyType == typeof(string),
            $"Band must be nullable string; got {bandProp.PropertyType.Name}.");
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
