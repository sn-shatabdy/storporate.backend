using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// STOR-66 role-grant coverage: <c>job-postings:manage</c> is Organization-only and
/// <c>job-postings:read</c> is Student-only; Administrator receives both through
/// <see cref="Permissions.All"/> with no carve-out.
/// </summary>
public class JobPostingGrantsTests
{
    [Fact]
    public void Permissions_ExposeTheExpectedLiteralsExactly()
    {
        Assert.Equal("job-postings:manage", Permissions.JobPostings.Manage);
        Assert.Equal("job-postings:read", Permissions.JobPostings.Read);

        var literals = Permissions.All
            .Where(p => p.StartsWith("job-postings:", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "job-postings:manage", "job-postings:read" }, literals);
    }

    [Fact]
    public void Organization_GrantsManageButNotRead()
    {
        var grants = SystemRoles.Grants[SystemRoles.Organization];
        Assert.Contains(Permissions.JobPostings.Manage, grants);
        Assert.DoesNotContain(Permissions.JobPostings.Read, grants);
    }

    [Fact]
    public void Student_GrantsReadButNotManage()
    {
        var grants = SystemRoles.Grants[SystemRoles.Student];
        Assert.Contains(Permissions.JobPostings.Read, grants);
        Assert.DoesNotContain(Permissions.JobPostings.Manage, grants);
    }

    [Theory]
    [InlineData("University")]
    [InlineData("Club")]
    public void UniversityAndClub_GrantNeitherJobPostingPermission(string role)
    {
        var grants = SystemRoles.Grants[role];
        Assert.DoesNotContain(Permissions.JobPostings.Manage, grants);
        Assert.DoesNotContain(Permissions.JobPostings.Read, grants);
    }

    [Fact]
    public void Administrator_GrantsBoth_WithNoCarveOut()
    {
        var grants = SystemRoles.Grants[SystemRoles.Administrator];
        Assert.Contains(Permissions.JobPostings.Manage, grants);
        Assert.Contains(Permissions.JobPostings.Read, grants);
        Assert.DoesNotContain(Permissions.JobPostings.Manage, SystemRoles.AdministratorExcludedFromAll);
        Assert.DoesNotContain(Permissions.JobPostings.Read, SystemRoles.AdministratorExcludedFromAll);
    }
}
