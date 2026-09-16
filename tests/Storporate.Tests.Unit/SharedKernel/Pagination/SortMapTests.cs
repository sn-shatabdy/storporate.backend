using Storporate.SharedKernel.Pagination;

namespace Storporate.Tests.Unit.SharedKernel.Pagination;

/// <summary>
/// Unit coverage for <see cref="SortMap{T}"/>: registration dedupes, the default
/// fallback applies when <c>SortBy</c> is null, an unknown key returns null, and a
/// missing <c>TieBreakBy</c> is enforced at <c>TryApply</c> time.
/// </summary>
public class SortMapTests
{
    [Fact]
    public void TryApply_KnownKey_AppliesOrderByAndTieBreaker()
    {
        var map = BuildMap();

        var ordered = map.TryApply(
            SampleData,
            new PageSpec(PageNumber: 1, PageSize: 10, SortBy: "name", SortDescending: false));

        Assert.NotNull(ordered);
        Assert.Equal(new[] { "alpha", "bravo", "charlie", "delta" }, ordered.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void TryApply_KnownKey_SortDescendingAppliesOrderByDescending()
    {
        var map = BuildMap();

        var ordered = map.TryApply(
            SampleData,
            new PageSpec(PageNumber: 1, PageSize: 10, SortBy: "name", SortDescending: true));

        Assert.NotNull(ordered);
        Assert.Equal(new[] { "delta", "charlie", "bravo", "alpha" }, ordered.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void TryApply_NullSortBy_FallsBackToDefaultKey()
    {
        var map = BuildMap();

        var ordered = map.TryApply(
            SampleData,
            new PageSpec(PageNumber: 1, PageSize: 10, SortBy: null, SortDescending: false));

        Assert.NotNull(ordered);
        // Default key is "name", so the result is alphabetically ordered: alpha, bravo,
        // charlie, delta. TieBreakBy(Id) would only matter if two rows shared a Name,
        // which these distinct test rows don't.
        Assert.Equal(new[] { "alpha", "bravo", "charlie", "delta" }, ordered.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void TryApply_UnknownSortBy_ReturnsNull()
    {
        var map = BuildMap();

        var ordered = map.TryApply(
            SampleData,
            new PageSpec(PageNumber: 1, PageSize: 10, SortBy: "garbage", SortDescending: false));

        Assert.Null(ordered);
    }

    [Fact]
    public void TryApply_KeyMatchIsCaseInsensitive()
    {
        var map = BuildMap();

        var ordered = map.TryApply(
            SampleData,
            new PageSpec(PageNumber: 1, PageSize: 10, SortBy: "NAME", SortDescending: false));

        Assert.NotNull(ordered);
        Assert.Equal(new[] { "alpha", "bravo", "charlie", "delta" }, ordered.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Register_DuplicateKeyCaseInsensitive_ThrowsAtRegistrationTime()
    {
        // Duplicate detection runs at Register() time so a typo or copy-paste mistake
        // fails the build / setup, not the first query that exercises the map.
        var map = new SortMap<Sample>()
            .Register("name", x => x.Name);

        Assert.Throws<InvalidOperationException>(() => map.Register("Name", x => x.Name));
    }

    [Fact]
    public void Default_OnUnregisteredKey_Throws()
    {
        // Default is now strictly mark-default: callers must Register first. The previous
        // "silently re-register" overload was a footgun because it hid typos in the
        // default key from the developer. This test pins the throw so a regression to
        // silent re-registration fails the test.
        var map = new SortMap<Sample>();

        Assert.Throws<InvalidOperationException>(() => map.Default("name"));
    }

    [Fact]
    public void Default_OnRegisteredKey_DoesNotThrow()
    {
        var map = new SortMap<Sample>()
            .Register("name", x => x.Name);

        map.Default("name");
        Assert.Equal("name", map.DefaultKey);
    }

    [Fact]
    public void Keys_ReflectsRegisteredKeysInRegistrationOrder()
    {
        // Keys is the single source of truth for "what does this map support?" — the
        // pagination test asserts it shows up in UnknownSortKeyException.SupportedKeys,
        // but this test pins the registration-order invariant locally so a future
        // refactor that sorts or de-dupes the list is caught here.
        var map = new SortMap<Sample>()
            .Register("name", x => x.Name)
            .Map("priority", x => x.Priority)
            .Map("id", x => x.Id);

        Assert.Equal(new[] { "name", "priority", "id" }, map.Keys);
    }

    [Fact]
    public void TryApply_WithoutTieBreakBy_Throws()
    {
        // The whole point of TieBreakBy is to catch the "I forgot to set up the stable
        // secondary sort" bug. TryApply must throw, not silently produce an unstable
        // ordered queryable.
        var map = new SortMap<Sample>()
            .Map("name", x => x.Name);

        Assert.Throws<InvalidOperationException>(() => map.TryApply(
            SampleData,
            new PageSpec(PageNumber: 1, PageSize: 10, SortBy: "name", SortDescending: false)));
    }

    [Fact]
    public void Register_NullOrEmptyKey_Throws()
    {
        var map = new SortMap<Sample>();

        Assert.Throws<ArgumentNullException>(() => map.Register(null!, x => x.Name));
        Assert.Throws<ArgumentException>(() => map.Register(string.Empty, x => x.Name));
    }

    private static SortMap<Sample> BuildMap() =>
        new SortMap<Sample>()
            .Register("name", x => x.Name)
            .Default("name")
            .Map("priority", x => x.Priority)
            .TieBreakBy(x => x.Id);

    private static readonly IQueryable<Sample> SampleData = new[]
    {
        new Sample("charlie", 1, 4),
        new Sample("alpha", 2, 1),
        new Sample("delta", 1, 3),
        new Sample("bravo", 1, 2),
    }.AsQueryable();

    private sealed record Sample(string Name, int Priority, int Id);
}
