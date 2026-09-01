using FoundryGate.Domain.Common;

namespace FoundryGate.Tests.Predeployment.Domain;

public class PagedResultTests
{
    [Theory]
    [InlineData(0, 25, 0)]
    [InlineData(1, 25, 1)]
    [InlineData(25, 25, 1)]
    [InlineData(26, 25, 2)]
    [InlineData(250, 25, 10)]
    [InlineData(251, 25, 11)]
    public void TotalPages_is_the_ceiling_of_totalCount_over_pageSize(int totalCount, int pageSize, int expectedTotalPages)
    {
        var result = new PagedResult<string>([], totalCount, Page: 1, pageSize);

        Assert.Equal(expectedTotalPages, result.TotalPages);
    }

    [Fact]
    public void Empty_factory_produces_a_zero_item_zero_count_page()
    {
        PagedResult<int> result = PagedResult<int>.Empty(page: 2, pageSize: 10);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(0, result.TotalPages);
    }
}
