using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// STOR-69 role-grant coverage: <c>club-profile:manage</c> is Club-only and <c>club-profile:read</c>
/// is Organization-only; Administrator receives both through <see cref="Permissions.All"/> with no carve-out.
/// </summary>
public class ClubProfileGrantsTests
{
    [Fact]
    public void Permissions_ExposeTheExpectedLiteralsExactly()
    {
        Assert.Equal("club-profile:manage", Permissions.ClubProfiles.Manage);
        Assert.Equal("club-profile:read", Permissions.ClubProfiles.Read);

        var literals = Permissions.All
            .Where(p => p.StartsWith("club-profile:", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "club-profile:manage", "club-profile:read" }, literals);
    }

    [Fact]
    public void Club_GrantsManageButNotRead_AndKeepsJobsRead()
    {
        var grants = SystemRoles.Grants[SystemRoles.Club];
        Assert.Contains(Permissions.ClubProfiles.Manage, grants);
        Assert.DoesNotContain(Permissions.ClubProfiles.Read, grants);
        Assert.Contains(Permissions.Jobs.Read, grants);
    }

    [Fact]
    public void Organization_GrantsReadButNotManage()
    {
        var grants = SystemRoles.Grants[SystemRoles.Organization];
        Assert.Contains(Permissions.ClubProfiles.Read, grants);
        Assert.DoesNotContain(Permissions.ClubProfiles.Manage, grants);
    }

    [Theory]
    [InlineData("Student")]
    [InlineData("University")]
    public void StudentAndUniversity_GrantNeitherClubProfilePermission(string role)
    {
        var grants = SystemRoles.Grants[role];
        Assert.DoesNotContain(Permissions.ClubProfiles.Manage, grants);
        Assert.DoesNotContain(Permissions.ClubProfiles.Read, grants);
    }

    [Fact]
    public void Administrator_GrantsBoth_WithNoCarveOut()
    {
        var grants = SystemRoles.Grants[SystemRoles.Administrator];
        Assert.Contains(Permissions.ClubProfiles.Manage, grants);
        Assert.Contains(Permissions.ClubProfiles.Read, grants);
        Assert.DoesNotContain(Permissions.ClubProfiles.Manage, SystemRoles.AdministratorExcludedFromAll);
        Assert.DoesNotContain(Permissions.ClubProfiles.Read, SystemRoles.AdministratorExcludedFromAll);
    }
}
