using FoundryGate.Domain.Common;

namespace FoundryGate.Tests.Predeployment.Domain;

public class PagedRequestTests
{
    [Fact]
    public void Defaults_are_already_in_bounds()
    {
        var request = new PagedRequest();

        Assert.Equal(1, request.Page);
        Assert.Equal(PagedRequest.DefaultPageSize, request.PageSize);

        PagedRequest clamped = request.Clamp();
        Assert.Equal(request, clamped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Clamp_normalizes_a_non_positive_page_to_1(int page)
    {
        var request = new PagedRequest(page, PagedRequest.DefaultPageSize);

        PagedRequest clamped = request.Clamp();

        Assert.Equal(1, clamped.Page);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Clamp_normalizes_a_non_positive_pageSize_to_the_default(int pageSize)
    {
        var request = new PagedRequest(1, pageSize);

        PagedRequest clamped = request.Clamp();

        Assert.Equal(PagedRequest.DefaultPageSize, clamped.PageSize);
    }

    [Fact]
    public void Clamp_caps_an_oversized_pageSize_at_the_maximum()
    {
        var request = new PagedRequest(1, PagedRequest.MaxPageSize + 1000);

        PagedRequest clamped = request.Clamp();

        Assert.Equal(PagedRequest.MaxPageSize, clamped.PageSize);
    }

    [Fact]
    public void Clamp_leaves_an_in_range_pageSize_untouched()
    {
        var request = new PagedRequest(3, 50);

        PagedRequest clamped = request.Clamp();

        Assert.Equal(50, clamped.PageSize);
        Assert.Equal(3, clamped.Page);
    }
}
