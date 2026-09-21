using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Architecture;

/// <summary>
/// STOR-43 Phase 1: pin the non-tenant shape of
/// <see cref="TalentIndexEntry"/>. The table is read by every
/// <c>SystemRoles.Organization</c> account (Phase 2 search) and only
/// written by the <c>RefreshTalentIndexEntryProcessor</c> background job,
/// so the EF Core global query filter must skip it and the
/// <c>account_scoped</c> RLS migration must leave it alone. A future
/// contributor who accidentally adds <see cref="IAccountScoped"/> to
/// <see cref="TalentIndexEntry"/> would silently hide every entry from
/// every employer and re-fail this gate.
/// </summary>
/// <remarks>
/// Lives in <c>Tests.Architecture</c> (rather than a unit test) because
/// the rule is about the entity's relationship to the EF model, not the
/// behavior of any one class — same rationale as
/// <see cref="ModuleIsolationTests"/>.
/// </remarks>
public class TalentIndexIsolationTests
{
    [Fact]
    public void TalentIndexEntry_DoesNotImplementIAccountScoped()
    {
        Assert.False(
            typeof(IAccountScoped).IsAssignableFrom(typeof(TalentIndexEntry)),
            "TalentIndexEntry must NOT implement IAccountScoped. Phase 2 reads it "
            + "from every Organization account, so the global query filter would "
            + "silently hide every row. If you find yourself wanting to add the "
            + "interface, the right answer is a separate scoped row + a join key.");
    }

    /// <summary>
    /// Sibling guard: <see cref="StudentSearchProfile"/> IS tenant-scoped
    /// (the student owns their profile), so the RLS migration installs a
    /// <c>CREATE POLICY account_scoped</c> on its table. A future refactor
    /// that flips the interface implementation on or off must fail the
    /// present test (or <see cref="AccountScopedRowLevelSecurityTests"/>) —
    /// this test exists to make the intent explicit and to keep the pair
    /// of "TalentIndexEntry is global" / "StudentSearchProfile is scoped"
    /// assertions co-located so a reviewer can see both at once.
    /// </summary>
    [Fact]
    public void StudentSearchProfile_DoesImplementIAccountScoped()
    {
        Assert.True(
            typeof(IAccountScoped).IsAssignableFrom(typeof(StudentSearchProfile)),
            "StudentSearchProfile must implement IAccountScoped — the EF Core global "
            + "query filter, the save-time RowLevelSecurityInterceptor, and the "
            + "account_scoped RLS policy all key on its AccountId. The companion "
            + "non-tenant table is TalentIndexEntry; never both on the same row.");
    }
}