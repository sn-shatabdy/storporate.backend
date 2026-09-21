using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// STOR-71 role-grant coverage: <c>sponsorship-matches:read</c> is granted to both Organization and
/// Club; Administrator receives it through <see cref="Permissions.All"/> with no carve-out.
/// </summary>
public class SponsorshipMatchingGrantsTests
{
    [Fact]
    public void Permission_ExposesTheExpectedLiteralExactly()
    {
        Assert.Equal("sponsorship-matches:read", Permissions.SponsorshipMatching.Read);
        Assert.Equal(
            new[] { "sponsorship-matches:read" },
            Permissions.All.Where(p => p.StartsWith("sponsorship-matches:", StringComparison.Ordinal)).ToArray());
    }

    [Theory]
    [InlineData("Organization")]
    [InlineData("Club")]
    public void OrganizationAndClub_GrantRead(string role) =>
        Assert.Contains(Permissions.SponsorshipMatching.Read, SystemRoles.Grants[role]);

    [Theory]
    [InlineData("Student")]
    [InlineData("University")]
    public void StudentAndUniversity_DoNotGrantRead(string role) =>
        Assert.DoesNotContain(Permissions.SponsorshipMatching.Read, SystemRoles.Grants[role]);

    [Fact]
    public void Administrator_GrantsRead_WithNoCarveOut()
    {
        Assert.Contains(Permissions.SponsorshipMatching.Read, SystemRoles.Grants[SystemRoles.Administrator]);
        Assert.DoesNotContain(Permissions.SponsorshipMatching.Read, SystemRoles.AdministratorExcludedFromAll);
    }
}
