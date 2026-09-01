using FoundryGate.Data.Entities;
using FoundryGate.Data.Extensions;
using FoundryGate.Domain.Common;

namespace FoundryGate.Tests.Predeployment.Data;

/// <summary>The shared <c>ToPagedAsync</c> helper every paged list endpoint depends on.</summary>
public class QueryableExtensionsTests : InMemoryDatabaseTest
{
    [Fact]
    public async Task ToPagedAsync_returns_the_requested_page_with_the_total_count_across_all_pages()
    {
        await SeedUsersAsync(7);

        var page = await Context.Users
            .OrderBy(u => u.DisplayName)
            .Select(u => u.DisplayName)
            .ToPagedAsync(new PagedRequest(Page: 2, PageSize: 3), CancellationToken.None);

        Assert.Equal(7, page.TotalCount);
        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.PageSize);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(["User 3", "User 4", "User 5"], page.Items);
    }

    [Fact]
    public async Task ToPagedAsync_clamps_out_of_range_paging_instead_of_failing()
    {
        await SeedUsersAsync(3);

        var page = await Context.Users
            .OrderBy(u => u.DisplayName)
            .Select(u => u.DisplayName)
            .ToPagedAsync(new PagedRequest(Page: 0, PageSize: PagedRequest.MaxPageSize + 1), CancellationToken.None);

        Assert.Equal(1, page.Page);
        Assert.Equal(PagedRequest.MaxPageSize, page.PageSize);
        Assert.Equal(3, page.Items.Count);
    }

    [Fact]
    public async Task ToPagedAsync_past_the_last_page_returns_an_empty_page_with_the_true_total()
    {
        await SeedUsersAsync(2);

        var page = await Context.Users
            .OrderBy(u => u.UserId)
            .ToPagedAsync(new PagedRequest(Page: 5, PageSize: 10), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(5, page.Page);
    }

    private async Task SeedUsersAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Context.Users.Add(new User
            {
                EntraObjectId = Guid.NewGuid().ToString(),
                DisplayName = $"User {i}",
                Email = $"user{i}@contoso.test",
            });
        }

        await Context.SaveChangesAsync();
    }
}
