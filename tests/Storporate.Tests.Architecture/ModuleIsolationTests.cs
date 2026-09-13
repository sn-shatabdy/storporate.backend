using System.Reflection;
using NetArchTest.Rules;
using Storporate.Modules.PlatformFoundations;

namespace Storporate.Tests.Architecture;

/// <summary>
/// Enforces the vertical-slice module boundary: no module under <c>Storporate.Modules.*</c> may
/// depend on a sibling module's namespace. Mirrors Docomate's own
/// <c>Module_ShouldNotDependOn_SiblingModule</c> pattern, reimplemented here against the
/// Storporate module list.
/// </summary>
public class ModuleIsolationTests
{
    private const string ModulesRootNamespace = "Storporate.Modules";

    private static readonly Assembly ModulesAssembly = typeof(DependencyInjection).Assembly;

    public static readonly string[] ModuleNames =
    [
        "EvidenceEngine",
        "TrustIntegrityNetwork",
        "StudentGrowthExperience",
        "VerifiableCredentialsDataRights",
        "DiscoveryHiring",
        "InstitutionalClubNetwork",
        "PlatformFoundations",
        "SecurityGovernance",
    ];

    public static IEnumerable<object[]> ModuleNameTheoryData() =>
        ModuleNames.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(ModuleNameTheoryData))]
    public void Module_ShouldNotDependOn_SiblingModule(string moduleName)
    {
        var ownNamespace = $"{ModulesRootNamespace}.{moduleName}";

        var siblingNamespaces = ModuleNames
            .Where(name => name != moduleName)
            .Select(name => $"{ModulesRootNamespace}.{name}")
            .ToArray();

        var result = Types.InAssembly(ModulesAssembly)
            .That()
            .ResideInNamespace(ownNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(siblingNamespaces)
            .GetResult();

        var failingTypeNames = result.FailingTypes?.Select(type => type.FullName) ?? [];

        Assert.True(
            result.IsSuccessful,
            $"Module '{moduleName}' has a forbidden dependency on a sibling module: {string.Join(", ", failingTypeNames)}");
    }
}
