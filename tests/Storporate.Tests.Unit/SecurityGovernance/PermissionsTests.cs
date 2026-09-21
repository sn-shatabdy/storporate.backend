using System.Reflection;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// Unit coverage for the permission catalog's reflection-driven <see cref="Permissions.All"/>
/// surface. These tests guard the single-source-of-truth guarantee: every <c>const string</c>
/// defined in any nested <see cref="Permissions"/> area is materialized into
/// <see cref="Permissions.All"/> exactly once, no duplicates, and the Administrator grant set
/// equals <see cref="Permissions.All"/> by construction (rather than being a hand-maintained
/// parallel list that can drift).
/// </summary>
public class PermissionsTests
{
    [Fact]
    public void All_ContainsEveryConstStringDefinedAcrossNestedClasses()
    {
        var all = Permissions.All;

        Assert.NotEmpty(all);

        // Walk every nested public static type under Permissions and collect every
        // public const string literal — same set the BuildAll() reflection scan uses,
        // re-implemented here independently so the test would still pass against an
        // accidental rewrite of BuildAll().
        var definedConstants = new List<string>();
        foreach (var nestedType in typeof(Permissions).GetNestedTypes(BindingFlags.Public | BindingFlags.Static))
        {
            foreach (var field in nestedType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field.IsLiteral && field.FieldType == typeof(string) && field.GetRawConstantValue() is string value)
                {
                    definedConstants.Add(value);
                }
            }
        }

        Assert.NotEmpty(definedConstants);

        // Every independently-enumerated const must appear in Permissions.All.
        var allSet = new HashSet<string>(all, StringComparer.Ordinal);
        foreach (var permission in definedConstants)
        {
            Assert.Contains(permission, allSet);
        }

        // And Permissions.All must not contain any value that wasn't defined as a const —
        // i.e. it is exactly the set of consts (modulo deduplication, verified below).
        var definedSet = new HashSet<string>(definedConstants, StringComparer.Ordinal);
        foreach (var permission in all)
        {
            Assert.Contains(permission, definedSet);
        }
    }

    [Fact]
    public void All_HasNoDuplicates()
    {
        var distinct = new HashSet<string>(Permissions.All, StringComparer.Ordinal);

        Assert.Equal(Permissions.All.Count, distinct.Count);
    }

    [Fact]
    public void All_IsOrdinalSorted()
    {
        // BuildAll() uses a SortedSet<string>(StringComparer.Ordinal), so the materialized
        // IReadOnlyList<string> must already be in ascending ordinal order. This test
        // catches accidental future refactors (e.g. switching to HashSet + OrderBy) that
        // silently change the on-the-wire ordering relied on by diagnostics / logs.
        for (var index = 1; index < Permissions.All.Count; index++)
        {
            Assert.True(
                string.CompareOrdinal(Permissions.All[index - 1], Permissions.All[index]) < 0,
                $"Permissions.All is not ordinally sorted at index {index}: " +
                $"'{Permissions.All[index - 1]}' should precede '{Permissions.All[index]}'.");
        }
    }

    [Fact]
    public void Jobs_ReadAndWriteAreExposedAsConstStrings()
    {
        // Lock down the literal strings of the first round of permissions so a typo /
        // rename in Permissions.Jobs.{Read,Write} is caught as a test failure rather than
        // a silent contract change against SystemRoles / PolicyProvider consumers.
        Assert.Equal("jobs:read", Permissions.Jobs.Read);
        Assert.Equal("jobs:write", Permissions.Jobs.Write);
    }

    [Fact]
    public void Administrator_GrantsExactlyEqualPermissionsAllExceptCarveouts()
    {
        // SystemRoles.Grants["Administrator"] starts as Permissions.All
        // by construction, then has SystemRoles.AdministratorExcludedFromAll
        // carved out — see SystemRoles.BuildGrants. The set semantics
        // this test pins:
        //   * every permission in Permissions.All is granted to Administrator
        //     UNLESS it appears on the carve-out list;
        //   * every carved-out permission is NOT granted;
        //   * no permission is granted that is not in Permissions.All.
        // The carve-out list is hand-maintained for permissions whose
        // surface should be deliberately narrowed from Administrator
        // (e.g. STOR-44 Phase 2's CandidateReview.Read, which reads
        // another student's TalentIndexEntry even under Administrator's
        // workspace-isolation bypass). The set equality below catches a
        // future drift on either side — the carve-out widens here AND
        // Administrator grants change in one PR.
        Assert.True(SystemRoles.Grants.TryGetValue(SystemRoles.Administrator, out var administratorGrants));

        var allSet = new HashSet<string>(Permissions.All, StringComparer.Ordinal);
        var excluded = SystemRoles.AdministratorExcludedFromAll;

        // No permission outside Permissions.All is granted to Administrator.
        foreach (var permission in administratorGrants)
        {
            Assert.Contains(permission, allSet);
        }

        // Every carve-out is absent from Administrator's grant set.
        foreach (var permission in excluded)
        {
            Assert.DoesNotContain(permission, administratorGrants);
        }

        // And every other permission is granted.
        var expected = new HashSet<string>(allSet, StringComparer.Ordinal);
        expected.ExceptWith(excluded);
        Assert.Equal(expected.Count, administratorGrants.Count);
        foreach (var permission in expected)
        {
            Assert.Contains(permission, administratorGrants);
        }
    }
}
