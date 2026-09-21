using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// STOR-68 role-grant coverage: <c>outreach:send</c> is Organization-only and <c>outreach:respond</c>
/// is Student-only; Administrator receives both through <see cref="Permissions.All"/> with no carve-out.
/// </summary>
public class OutreachGrantsTests
{
    [Fact]
    public void Permissions_ExposeTheExpectedLiteralsExactly()
    {
        Assert.Equal("outreach:send", Permissions.Outreach.Send);
        Assert.Equal("outreach:respond", Permissions.Outreach.Respond);

        var literals = Permissions.All
            .Where(p => p.StartsWith("outreach:", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "outreach:respond", "outreach:send" }, literals);
    }

    [Fact]
    public void Student_GrantsRespondButNotSend()
    {
        var grants = SystemRoles.Grants[SystemRoles.Student];
        Assert.Contains(Permissions.Outreach.Respond, grants);
        Assert.DoesNotContain(Permissions.Outreach.Send, grants);
    }

    [Fact]
    public void Organization_GrantsSendButNotRespond()
    {
        var grants = SystemRoles.Grants[SystemRoles.Organization];
        Assert.Contains(Permissions.Outreach.Send, grants);
        Assert.DoesNotContain(Permissions.Outreach.Respond, grants);
    }

    [Theory]
    [InlineData("University")]
    [InlineData("Club")]
    public void UniversityAndClub_GrantNeitherOutreachPermission(string role)
    {
        var grants = SystemRoles.Grants[role];
        Assert.DoesNotContain(Permissions.Outreach.Send, grants);
        Assert.DoesNotContain(Permissions.Outreach.Respond, grants);
    }

    [Fact]
    public void Administrator_GrantsBoth_WithNoCarveOut()
    {
        var grants = SystemRoles.Grants[SystemRoles.Administrator];
        Assert.Contains(Permissions.Outreach.Send, grants);
        Assert.Contains(Permissions.Outreach.Respond, grants);
        Assert.DoesNotContain(Permissions.Outreach.Send, SystemRoles.AdministratorExcludedFromAll);
        Assert.DoesNotContain(Permissions.Outreach.Respond, SystemRoles.AdministratorExcludedFromAll);
    }
}
