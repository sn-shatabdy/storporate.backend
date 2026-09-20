using Storporate.SharedKernel.Authorization;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// STOR-43 Phase 1 role-grant coverage. Locks down the four new permission strings and
/// the per-role grant sets that depend on them — anything that would silently change who
/// can run a talent search or read/write a student's opt-in profile. Companion to
/// <see cref="PermissionsTests"/> (which guards the catalog) and
/// <see cref="PermissionServiceTests"/> (which guards the lookup path); this file is
/// focused on the role-to-permission mapping itself so a future grant drift is caught
/// here rather than as a downstream endpoint authorization surprise.
/// </summary>
public class TalentSearchGrantsTests
{
    private static readonly IReadOnlyList<string> NewPermissionLiterals = new[]
    {
        "talent-search:create",
        "talent-search:read",
        "searchable-profile:read",
        "searchable-profile:update",
    };

    [Fact]
    public void Permissions_ExposesTheFourNewLiteralsExactly()
    {
        // Lock down the literal strings of the STOR-43 Phase 1 permissions so a typo /
        // rename on either the Permissions.TalentSearch.* or Permissions.SearchableProfile.*
        // const is caught as a test failure rather than as a silent contract change
        // against SystemRoles / PolicyProvider consumers.
        Assert.Equal("talent-search:create", Permissions.TalentSearch.Create);
        Assert.Equal("talent-search:read", Permissions.TalentSearch.Read);
        Assert.Equal("searchable-profile:read", Permissions.SearchableProfile.Read);
        Assert.Equal("searchable-profile:update", Permissions.SearchableProfile.Update);
    }

    [Fact]
    public void Permissions_AllContainsTheFourNewLiteralsAndNoExtra()
    {
        // Permissions.All is built by reflection over the nested permission classes, so
        // adding the new TalentSearch + SearchableProfile nested classes automatically
        // surfaces every literal here. This test pins the exact set the Administrator
        // grant set consumes — any future permission added under those nested classes will
        // flow through All and be checked by PermissionsTests.All_ContainsEveryConstString*
        // independently, but the exact four-string membership is the contract these role
        // grants depend on.
        var all = Permissions.All;

        foreach (var literal in NewPermissionLiterals)
        {
            Assert.Contains(literal, all);
        }

        // And the four Phase 1 literals are exactly the set we expect — no extras slipped
        // in under these two nested classes (e.g. a stray "talent-search:delete") that
        // would silently widen a role's grant set on a later story.
        var talentSearchLiterals = all.Where(p => p.StartsWith("talent-search:", StringComparison.Ordinal)).ToArray();
        var searchableProfileLiterals = all.Where(p => p.StartsWith("searchable-profile:", StringComparison.Ordinal)).ToArray();

        Assert.Equal(
            new[] { "talent-search:create", "talent-search:read" },
            talentSearchLiterals.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "searchable-profile:read", "searchable-profile:update" },
            searchableProfileLiterals.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Organization_GrantsTalentSearchCreateAndRead_ButNotSearchableProfile_AndKeepsExistingJobsGrants()
    {
        // STOR-43 Phase 1: Organization is the only hiring-side actor; talent-search is
        // hiring-side, so the Organization grant set widens to include TalentSearch.Create
        // + TalentSearch.Read and explicitly stays out of the student-side
        // SearchableProfile.* pair. The existing Jobs grant set (Read + Write) is also
        // asserted untouched so this story cannot accidentally remove a job-posting power
        // while adding the search one.
        var organizationGrants = SystemRoles.Grants[SystemRoles.Organization];

        Assert.Contains(Permissions.TalentSearch.Create, organizationGrants);
        Assert.Contains(Permissions.TalentSearch.Read, organizationGrants);

        Assert.DoesNotContain(Permissions.SearchableProfile.Read, organizationGrants);
        Assert.DoesNotContain(Permissions.SearchableProfile.Update, organizationGrants);

        Assert.Contains(Permissions.Jobs.Read, organizationGrants);
        Assert.Contains(Permissions.Jobs.Write, organizationGrants);
    }

    [Fact]
    public void Student_GrantsSearchableProfileReadAndUpdate_ButNotTalentSearch()
    {
        // STOR-43 Phase 1: only the owning Student reads or writes their own
        // SearchableProfile row, so the Student grant set widens here and explicitly
        // stays out of the hiring-side TalentSearch.* pair — a Student must never be
        // able to submit or read employer searches.
        var studentGrants = SystemRoles.Grants[SystemRoles.Student];

        Assert.Contains(Permissions.SearchableProfile.Read, studentGrants);
        Assert.Contains(Permissions.SearchableProfile.Update, studentGrants);

        Assert.DoesNotContain(Permissions.TalentSearch.Create, studentGrants);
        Assert.DoesNotContain(Permissions.TalentSearch.Read, studentGrants);
    }

    [Fact]
    public void University_DoesNotGrantAnyOfTheFourNewPermissions()
    {
        // University is institution-side and has no involvement with either the
        // employer talent-search surface or the student opt-in profile surface. Lock
        // the negative membership so a future story widening University's grant set
        // is forced to make a deliberate, reviewable change here.
        var universityGrants = SystemRoles.Grants[SystemRoles.University];

        foreach (var literal in NewPermissionLiterals)
        {
            Assert.DoesNotContain(literal, universityGrants);
        }
    }

    [Fact]
    public void Club_DoesNotGrantAnyOfTheFourNewPermissions()
    {
        // Club mirrors University for STOR-43 Phase 1: no involvement with either
        // surface. Same negative-membership lock as University so widening either
        // role's grant set is a reviewable, test-failing change.
        var clubGrants = SystemRoles.Grants[SystemRoles.Club];

        foreach (var literal in NewPermissionLiterals)
        {
            Assert.DoesNotContain(literal, clubGrants);
        }
    }

    [Fact]
    public void Administrator_GrantsAllFourNewPermissions()
    {
        // Administrator's grant set equals Permissions.All by construction (see
        // SystemRoles.BuildGrants). This test asserts the property the rest of the
        // platform depends on — every new permission surfaces to the Administrator
        // automatically — specifically against the four STOR-43 Phase 1 literals so a
        // regression is reported against this story rather than as a generic
        // "Administrator_GrantsExactlyEqualPermissionsAll" failure.
        var administratorGrants = SystemRoles.Grants[SystemRoles.Administrator];

        foreach (var literal in NewPermissionLiterals)
        {
            Assert.Contains(literal, administratorGrants);
        }
    }
}
