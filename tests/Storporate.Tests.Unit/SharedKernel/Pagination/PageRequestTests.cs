using Storporate.SharedKernel.Pagination;

namespace Storporate.Tests.Unit.SharedKernel.Pagination;

/// <summary>
/// Unit coverage for <see cref="PageRequest.ToSpec"/>'s input normalization: every
/// pathological input the query string can produce (nulls, negatives, oversized
/// page sizes, hostile sort keys) is normalized into a safe, predictable
/// <see cref="PageSpec"/>.
/// </summary>
public class PageRequestTests
{
    [Fact]
    public void ToSpec_NullPageNumber_NormalizesToOne()
    {
        var request = new SamplePageRequest { PageNumber = null };

        var spec = request.ToSpec();

        Assert.Equal(1, spec.PageNumber);
    }

    [Fact]
    public void ToSpec_PageNumberBelowOne_NormalizesToOne()
    {
        var request = new SamplePageRequest { PageNumber = -3 };

        var spec = request.ToSpec();

        Assert.Equal(1, spec.PageNumber);
    }

    [Fact]
    public void ToSpec_NullPageSize_NormalizesToDefault()
    {
        var request = new SamplePageRequest { PageSize = null };

        var spec = request.ToSpec();

        Assert.Equal(PageRequest.DefaultPageSize, spec.PageSize);
    }

    [Fact]
    public void ToSpec_PageSizeBelowOne_NormalizesToDefault()
    {
        var request = new SamplePageRequest { PageSize = 0 };

        var spec = request.ToSpec();

        Assert.Equal(PageRequest.DefaultPageSize, spec.PageSize);
    }

    [Fact]
    public void ToSpec_PageSizeAboveMax_ClampsToMax()
    {
        var request = new SamplePageRequest { PageSize = 10_000 };

        var spec = request.ToSpec();

        Assert.Equal(PageRequest.MaxPageSize, spec.PageSize);
    }

    [Fact]
    public void ToSpec_PageSizeAtMax_PassesThroughUnchanged()
    {
        var request = new SamplePageRequest { PageSize = PageRequest.MaxPageSize };

        var spec = request.ToSpec();

        Assert.Equal(PageRequest.MaxPageSize, spec.PageSize);
    }

    [Fact]
    public void ToSpec_SortByAndSortDescending_PassThroughUnchanged()
    {
        var request = new SamplePageRequest { SortBy = "anything", SortDescending = true };

        var spec = request.ToSpec();

        Assert.Equal("anything", spec.SortBy);
        Assert.True(spec.SortDescending);
    }

    [Fact]
    public void Skip_AtPageOne_IsZero()
    {
        var spec = new PageSpec(PageNumber: 1, PageSize: 20, SortBy: null, SortDescending: false);

        Assert.Equal(0, spec.Skip);
    }

    [Fact]
    public void Skip_AtSubsequentPages_AdvancesByPageSize()
    {
        var spec = new PageSpec(PageNumber: 5, PageSize: 20, SortBy: null, SortDescending: false);

        Assert.Equal(80, spec.Skip);
    }

    [Fact]
    public void Skip_NeverGoesNegative_EvenForHostilePageNumber()
    {
        var spec = new PageSpec(PageNumber: -100, PageSize: 20, SortBy: null, SortDescending: false);

        Assert.Equal(0, spec.Skip);
    }

    [Fact]
    public void Skip_NeverOverflows_EvenForGiganticPageNumber()
    {
        // (int.MaxValue - PageSize) is the largest safe Skip value — Skip + Take must
        // fit in int. Verify the clamp catches anything larger.
        var spec = new PageSpec(PageNumber: int.MaxValue, PageSize: 20, SortBy: null, SortDescending: false);

        Assert.Equal(int.MaxValue - 20, spec.Skip);
    }

    [Fact]
    public void PagedResult_HasPrevious_IsFalseOnFirstPage()
    {
        var result = new PagedResult<int>(Items: [1], PageNumber: 1, PageSize: 10, TotalCount: 100);

        Assert.False(result.HasPrevious);
        Assert.True(result.HasNext);
        Assert.Equal(10, result.TotalPages);
    }

    [Fact]
    public void PagedResult_HasNext_IsFalseOnLastPage()
    {
        var result = new PagedResult<int>(Items: [1], PageNumber: 10, PageSize: 10, TotalCount: 100);

        Assert.True(result.HasPrevious);
        Assert.False(result.HasNext);
    }

    [Fact]
    public void PagedResult_TotalPages_IsZeroWhenEmpty()
    {
        var result = new PagedResult<int>(Items: [], PageNumber: 1, PageSize: 10, TotalCount: 0);

        Assert.Equal(0, result.TotalPages);
        Assert.False(result.HasNext);
        Assert.False(result.HasPrevious);
    }

    private sealed record SamplePageRequest : PageRequest;
}
