using Bunit;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;
using MudBlazor;

namespace FoundryGate.Tests.Predeployment.Web.Pages;

/// <summary>
/// <c>/users</c> (#51): the grid renders what <c>GET /users</c> returned, the toolbar's search
/// and status filter reach the query the client sends, and a 403 from the API turns the page
/// into <c>AccessDenied</c> rather than an empty grid.
/// </summary>
public class UsersPageTests : WebTestContext
{
    public UsersPageTests()
    {
        SignInAsAdmin();
        Api.ArrangeTiers(WebTestData.Tiers);
    }

    [Fact]
    public void Renders_a_row_per_user_with_the_tier_name_not_a_token_count()
    {
        Api.ArrangeUsers(
            WebTestData.User(userId: 1, displayName: "Dev Eloper", monthlyTokenQuota: 5_000_000),
            WebTestData.User(userId: 2, displayName: "Grace Hopper", isUnlimited: true, monthlyTokenQuota: null));

        var page = RenderPage<Users>();

        page.WaitForAssertion(() =>
        {
            var markup = page.Markup;
            Assert.Contains("Dev Eloper", markup, StringComparison.Ordinal);
            Assert.Contains("Grace Hopper", markup, StringComparison.Ordinal);

            // D-013: a budget is a tier, so the cell says "Standard", never "5,000,000".
            Assert.Contains("Standard", markup, StringComparison.Ordinal);
            Assert.Contains("Unlimited", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("5,000,000", markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Loads_the_first_page_with_no_filters_by_default()
    {
        var page = RenderPage<Users>();

        page.WaitForAssertion(() =>
        {
            var call = Assert.Single(Api.UserListCalls);
            Assert.Null(call.Query.Search);
            Assert.Null(call.Query.IsActive);
            Assert.Equal(1, call.Paging.Page);
        });
    }

    [Fact]
    public void Typing_in_the_search_box_re_queries_with_the_search_term()
    {
        var page = RenderPage<Users>();
        page.WaitForAssertion(() => Assert.NotEmpty(Api.UserListCalls));

        page.Find("input[data-testid=users-search]").Input("hopper");

        page.WaitForAssertion(() => Assert.Contains(Api.UserListCalls, c => c.Query.Search == "hopper"));
    }

    [Fact]
    public async Task A_search_from_a_later_page_starts_again_from_page_one()
    {
        // Two pages' worth, so the pager will actually move: page 3 of the old filter is rarely
        // page 3 of the new one, and a search that left the grid on page 2 shows an empty result
        // for a filter that matched plenty.
        Api.UsersResult = ApiCallResult<PagedResult<UserResponse>>.Ok(
            new PagedResult<UserResponse>([WebTestData.User(userId: 1)], TotalCount: 60, Page: 1, PageSize: 25));

        var page = RenderPage<Users>();
        page.WaitForAssertion(() => Assert.NotEmpty(Api.UserListCalls));

        var grid = page.FindComponent<MudDataGrid<UserResponse>>().Instance;
        await page.InvokeAsync(() => grid.NavigateTo(Page.Next));
        page.WaitForAssertion(() => Assert.Contains(Api.UserListCalls, c => c.Paging.Page == 2));

        Api.UserListCalls.Clear();
        page.Find("input[data-testid=users-search]").Input("hopper");

        page.WaitForAssertion(() =>
        {
            var searched = Api.UserListCalls.Where(c => c.Query.Search == "hopper").ToList();
            Assert.NotEmpty(searched);
            Assert.All(searched, c => Assert.Equal(1, c.Paging.Page));
        });
    }

    [Fact]
    public async Task A_filter_change_fetches_once_not_twice()
    {
        // NavigateTo(Page.First) reloads server data itself when ServerData is set, so calling it
        // and then ReloadServerData raced two identical responses onto the same grid.
        Api.UsersResult = ApiCallResult<PagedResult<UserResponse>>.Ok(
            new PagedResult<UserResponse>([WebTestData.User(userId: 1)], TotalCount: 60, Page: 1, PageSize: 25));

        var page = RenderPage<Users>();
        page.WaitForAssertion(() => Assert.NotEmpty(Api.UserListCalls));

        var grid = page.FindComponent<MudDataGrid<UserResponse>>().Instance;
        await page.InvokeAsync(() => grid.NavigateTo(Page.Next));
        page.WaitForAssertion(() => Assert.Contains(Api.UserListCalls, c => c.Paging.Page == 2));

        Api.UserListCalls.Clear();
        page.Find("input[data-testid=users-search]").Input("hopper");

        page.WaitForAssertion(() => Assert.NotEmpty(Api.UserListCalls));
        Assert.Single(Api.UserListCalls);
    }

    [Fact]
    public void Every_row_carries_a_control_a_keyboard_can_reach()
    {
        // RowClick is the only other way in, and a <tr> takes no focus and carries no role.
        Api.ArrangeUsers(WebTestData.User(userId: 1), WebTestData.User(userId: 2));

        var page = RenderPage<Users>();

        page.WaitForAssertion(() =>
        {
            Assert.Equal("users/1", page.Find("[data-testid=users-open-1]").GetAttribute("href"));
            Assert.Equal("users/2", page.Find("[data-testid=users-open-2]").GetAttribute("href"));
        });
    }

    [Fact]
    public void An_empty_list_says_so_rather_than_showing_a_bare_grid()
    {
        var page = RenderPage<Users>();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=users-empty]")));
    }

    [Fact]
    public void A_403_from_the_api_renders_access_denied()
    {
        Api.UsersResult = ApiCallResult<PagedResult<UserResponse>>
            .Fail(ApiCallStatus.Forbidden, "You don't have permission to do that.");

        var page = RenderPage<Users>();

        page.WaitForAssertion(() => Assert.Contains("Access denied", page.Markup, StringComparison.Ordinal));
    }
}
