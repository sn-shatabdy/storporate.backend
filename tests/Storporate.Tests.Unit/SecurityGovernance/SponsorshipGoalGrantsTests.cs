using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// STOR-70 role-grant coverage: <c>sponsorship-goals:manage</c> is Organization-only and
/// <c>sponsorship-goals:read</c> is Club-only; Administrator receives both through
/// <see cref="Permissions.All"/> with no carve-out.
/// </summary>
public class SponsorshipGoalGrantsTests
{
    [Fact]
    public void Permissions_ExposeTheExpectedLiteralsExactly()
    {
        Assert.Equal("sponsorship-goals:manage", Permissions.SponsorshipGoals.Manage);
        Assert.Equal("sponsorship-goals:read", Permissions.SponsorshipGoals.Read);

        var literals = Permissions.All
            .Where(p => p.StartsWith("sponsorship-goals:", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "sponsorship-goals:manage", "sponsorship-goals:read" }, literals);
    }

    [Fact]
    public void Organization_GrantsManageButNotRead()
    {
        var grants = SystemRoles.Grants[SystemRoles.Organization];
        Assert.Contains(Permissions.SponsorshipGoals.Manage, grants);
        Assert.DoesNotContain(Permissions.SponsorshipGoals.Read, grants);
    }

    [Fact]
    public void Club_GrantsReadButNotManage_AndKeepsItsOtherGrants()
    {
        var grants = SystemRoles.Grants[SystemRoles.Club];
        Assert.Contains(Permissions.SponsorshipGoals.Read, grants);
        Assert.DoesNotContain(Permissions.SponsorshipGoals.Manage, grants);
        Assert.Contains(Permissions.Jobs.Read, grants);
        Assert.Contains(Permissions.ClubProfiles.Manage, grants);
    }

    [Theory]
    [InlineData("Student")]
    [InlineData("University")]
    public void StudentAndUniversity_GrantNeitherSponsorshipGoalPermission(string role)
    {
        var grants = SystemRoles.Grants[role];
        Assert.DoesNotContain(Permissions.SponsorshipGoals.Manage, grants);
        Assert.DoesNotContain(Permissions.SponsorshipGoals.Read, grants);
    }

    [Fact]
    public void Administrator_GrantsBoth_WithNoCarveOut()
    {
        var grants = SystemRoles.Grants[SystemRoles.Administrator];
        Assert.Contains(Permissions.SponsorshipGoals.Manage, grants);
        Assert.Contains(Permissions.SponsorshipGoals.Read, grants);
        Assert.DoesNotContain(Permissions.SponsorshipGoals.Manage, SystemRoles.AdministratorExcludedFromAll);
        Assert.DoesNotContain(Permissions.SponsorshipGoals.Read, SystemRoles.AdministratorExcludedFromAll);
    }
}
