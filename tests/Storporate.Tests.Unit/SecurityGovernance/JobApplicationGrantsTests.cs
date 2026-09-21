using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// STOR-67 role-grant coverage: <c>job-applications:apply</c> is Student-only and
/// <c>job-applications:review</c> is Organization-only; Administrator receives both through
/// <see cref="Permissions.All"/> with no carve-out.
/// </summary>
public class JobApplicationGrantsTests
{
    [Fact]
    public void Permissions_ExposeTheExpectedLiteralsExactly()
    {
        Assert.Equal("job-applications:apply", Permissions.JobApplications.Apply);
        Assert.Equal("job-applications:review", Permissions.JobApplications.Review);

        var literals = Permissions.All
            .Where(p => p.StartsWith("job-applications:", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "job-applications:apply", "job-applications:review" }, literals);
    }

    [Fact]
    public void Student_GrantsApplyButNotReview()
    {
        var grants = SystemRoles.Grants[SystemRoles.Student];
        Assert.Contains(Permissions.JobApplications.Apply, grants);
        Assert.DoesNotContain(Permissions.JobApplications.Review, grants);
    }

    [Fact]
    public void Organization_GrantsReviewButNotApply()
    {
        var grants = SystemRoles.Grants[SystemRoles.Organization];
        Assert.Contains(Permissions.JobApplications.Review, grants);
        Assert.DoesNotContain(Permissions.JobApplications.Apply, grants);
    }

    [Theory]
    [InlineData("University")]
    [InlineData("Club")]
    public void UniversityAndClub_GrantNeitherJobApplicationPermission(string role)
    {
        var grants = SystemRoles.Grants[role];
        Assert.DoesNotContain(Permissions.JobApplications.Apply, grants);
        Assert.DoesNotContain(Permissions.JobApplications.Review, grants);
    }

    [Fact]
    public void Administrator_GrantsBoth_WithNoCarveOut()
    {
        var grants = SystemRoles.Grants[SystemRoles.Administrator];
        Assert.Contains(Permissions.JobApplications.Apply, grants);
        Assert.Contains(Permissions.JobApplications.Review, grants);
        Assert.DoesNotContain(Permissions.JobApplications.Apply, SystemRoles.AdministratorExcludedFromAll);
        Assert.DoesNotContain(Permissions.JobApplications.Review, SystemRoles.AdministratorExcludedFromAll);
    }
}
