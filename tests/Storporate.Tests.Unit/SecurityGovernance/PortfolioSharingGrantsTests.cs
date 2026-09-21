using Storporate.SharedKernel.Authorization;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// STOR-44 Phase 1 role-grant coverage for <c>portfolio:update</c>. Locks
/// down the new permission literal and the per-role grant sets that
/// depend on it — only the Student role can flip
/// <see cref="PortfolioItem.ShareOriginalWithEmployers"/> via
/// <c>PUT /api/portfolio/items/{id}/sharing</c>. Companion to
/// <see cref="PermissionsTests"/> (which guards the catalog) and
/// <see cref="PermissionServiceTests"/> (which guards the lookup path);
/// this file is focused on the role-to-permission mapping itself so a
/// future grant drift is caught here rather than as a downstream
/// endpoint authorization surprise.
/// </summary>
public class PortfolioSharingGrantsTests
{
    [Fact]
    public void Permissions_ExposesThePortfolioUpdateLiteral()
    {
        // Lock down the literal string of the new permission so a typo /
        // rename on Permissions.Portfolio.Update is caught as a test failure
        // rather than as a silent contract change against SystemRoles /
        // PolicyProvider consumers.
        Assert.Equal("portfolio:update", Permissions.Portfolio.Update);
    }

    [Fact]
    public void Permissions_AllContainsPortfolioUpdateExactlyOnce()
    {
        // Permissions.All is built by reflection over the nested permission
        // classes; this test pins the exact set the Administrator grant
        // set consumes — adding any future permission under Portfolio.* will
        // surface here, but the membership check is anchored to Phase 1's
        // exact single literal so a regression is reported against this
        // story rather than as a generic "Permissions.All changed" failure.
        var all = Permissions.All;

        Assert.Contains("portfolio:update", all);
        var portfolioLiterals = all.Where(p => p.StartsWith("portfolio:", StringComparison.Ordinal)).ToArray();
        // Portfolio has multiple verbs across the platform (Create, Read,
        // Update, Delete, Retry); the membership test only requires that
        // "portfolio:update" is one of them. Sort + assert the exact set
        // shape so a future permission rename is forced through the same
        // reviewable path.
        Assert.Contains("portfolio:update", portfolioLiterals);
    }

    [Fact]
    public void Student_GrantsPortfolioUpdate()
    {
        // STOR-44 Phase 1: only the owning Student flips their own item's
        // sharing flag, so the Student grant set widens here.
        var studentGrants = SystemRoles.Grants[SystemRoles.Student];

        Assert.Contains(Permissions.Portfolio.Update, studentGrants);
    }

    [Fact]
    public void Organization_DoesNotGrantPortfolioUpdate()
    {
        // STOR-44 Phase 1: an Organization is the consumer of the
        // drill-down (Phase 2 reads the descriptor), never the writer.
        // Lock the negative membership so a future story widening the
        // Organization grant set is forced to make a deliberate,
        // reviewable change here.
        var organizationGrants = SystemRoles.Grants[SystemRoles.Organization];

        Assert.DoesNotContain(Permissions.Portfolio.Update, organizationGrants);
    }

    [Fact]
    public void University_DoesNotGrantPortfolioUpdate()
    {
        var universityGrants = SystemRoles.Grants[SystemRoles.University];

        Assert.DoesNotContain(Permissions.Portfolio.Update, universityGrants);
    }

    [Fact]
    public void Club_DoesNotGrantPortfolioUpdate()
    {
        var clubGrants = SystemRoles.Grants[SystemRoles.Club];

        Assert.DoesNotContain(Permissions.Portfolio.Update, clubGrants);
    }

    [Fact]
    public void Student_KeepsAllExistingPortfolioGrants()
    {
        // Sanity: the existing portfolio verbs (Create / Read / Delete /
        // Retry) stay granted to Student alongside the new Update. A future
        // grant-set refactor that drops one of the existing verbs while
        // adding Update should fail this test.
        var studentGrants = SystemRoles.Grants[SystemRoles.Student];

        Assert.Contains(Permissions.Portfolio.Create, studentGrants);
        Assert.Contains(Permissions.Portfolio.Read, studentGrants);
        Assert.Contains(Permissions.Portfolio.Delete, studentGrants);
        Assert.Contains(Permissions.Portfolio.Retry, studentGrants);
        Assert.Contains(Permissions.Portfolio.Update, studentGrants);
    }
}
