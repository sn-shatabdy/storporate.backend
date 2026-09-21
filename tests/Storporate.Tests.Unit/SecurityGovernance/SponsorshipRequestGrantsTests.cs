using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// STOR-72 role-grant coverage: <c>sponsorship-requests:send</c> is Club-only and
/// <c>sponsorship-requests:respond</c> is Organization-only; Administrator receives both through
/// <see cref="Permissions.All"/> with no carve-out.
/// </summary>
public class SponsorshipRequestGrantsTests
{
    [Fact]
    public void Permissions_ExposeTheExpectedLiteralsExactly()
    {
        Assert.Equal("sponsorship-requests:send", Permissions.SponsorshipRequests.Send);
        Assert.Equal("sponsorship-requests:respond", Permissions.SponsorshipRequests.Respond);

        var literals = Permissions.All
            .Where(p => p.StartsWith("sponsorship-requests:", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "sponsorship-requests:respond", "sponsorship-requests:send" }, literals);
    }

    [Fact]
    public void Club_GrantsSendButNotRespond()
    {
        var grants = SystemRoles.Grants[SystemRoles.Club];
        Assert.Contains(Permissions.SponsorshipRequests.Send, grants);
        Assert.DoesNotContain(Permissions.SponsorshipRequests.Respond, grants);
    }

    [Fact]
    public void Organization_GrantsRespondButNotSend()
    {
        var grants = SystemRoles.Grants[SystemRoles.Organization];
        Assert.Contains(Permissions.SponsorshipRequests.Respond, grants);
        Assert.DoesNotContain(Permissions.SponsorshipRequests.Send, grants);
    }

    [Theory]
    [InlineData("Student")]
    [InlineData("University")]
    public void StudentAndUniversity_GrantNeitherPermission(string role)
    {
        var grants = SystemRoles.Grants[role];
        Assert.DoesNotContain(Permissions.SponsorshipRequests.Send, grants);
        Assert.DoesNotContain(Permissions.SponsorshipRequests.Respond, grants);
    }

    [Fact]
    public void Administrator_GrantsBoth_WithNoCarveOut()
    {
        var grants = SystemRoles.Grants[SystemRoles.Administrator];
        Assert.Contains(Permissions.SponsorshipRequests.Send, grants);
        Assert.Contains(Permissions.SponsorshipRequests.Respond, grants);
        Assert.DoesNotContain(Permissions.SponsorshipRequests.Send, SystemRoles.AdministratorExcludedFromAll);
        Assert.DoesNotContain(Permissions.SponsorshipRequests.Respond, SystemRoles.AdministratorExcludedFromAll);
    }
}
