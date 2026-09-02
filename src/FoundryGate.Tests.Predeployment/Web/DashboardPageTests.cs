using Bunit;
using FoundryGate.Domain.Dashboard.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// <c>/dashboard</c> (#54): the stat cards, the pending-request link, the top-consumers grid, the
/// 60-second refresh loop, and the pending count it publishes for the nav badge.
/// </summary>
public class DashboardPageTests : WebTestContext
{
    public DashboardPageTests() => SignInAsAdmin();

    [Fact]
    public void Shows_the_four_stat_cards_from_the_summary()
    {
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(
            totalUserCount: 120,
            activeUserCount: 118,
            unlimitedUserCount: 4,
            pendingRequestCount: 3,
            totalTokensUsed: 987_654_321));

        var page = RenderPage<Dashboard>();

        Assert.Contains("120", page.Find("[data-testid='stat-total-users']").TextContent, StringComparison.Ordinal);
        Assert.Contains("118", page.Find("[data-testid='stat-active-users']").TextContent, StringComparison.Ordinal);
        Assert.Contains("4 unlimited", page.Find("[data-testid='stat-active-users']").TextContent, StringComparison.Ordinal);
        Assert.Contains("3", page.Find("[data-testid='stat-pending-requests']").TextContent, StringComparison.Ordinal);
        Assert.Contains("987,654,321", page.Find("[data-testid='stat-tokens-used']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pending_count_links_into_the_filtered_review_queue()
    {
        var page = RenderPage<Dashboard>();

        Assert.Equal("requests?status=Pending", page.Find("[data-testid='stat-pending-link']").GetAttribute("href"));
    }

    [Fact]
    public void Publishes_the_pending_count_for_the_nav_badge()
    {
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(pendingRequestCount: 5));

        _ = RenderPage<Dashboard>();

        Assert.Equal(5, Services.GetRequiredService<DashboardStateService>().PendingRequestCount);
    }

    [Fact]
    public void Top_consumers_render_with_a_usage_bar()
    {
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(
            topConsumers: [WebTestData.Consumer("Heavy User", 4_900_000, 5_000_000, 98)]));

        var page = RenderPage<Dashboard>();

        var grid = page.Find("[data-testid='top-consumers-grid']").TextContent;
        Assert.Contains("Heavy User", grid, StringComparison.Ordinal);
        Assert.Contains("4,900,000", grid, StringComparison.Ordinal);
        Assert.Contains("98%", grid, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlimited_consumer_shows_a_chip_instead_of_a_bar()
    {
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(
            topConsumers: [WebTestData.Consumer("Unbounded Ursula", 12_000_000, null, null)]));

        var page = RenderPage<Dashboard>();

        var grid = page.Find("[data-testid='top-consumers-grid']").TextContent;
        Assert.Contains("Unlimited", grid, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_period_says_so_rather_than_rendering_an_empty_grid()
    {
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(topConsumers: []));

        var page = RenderPage<Dashboard>();

        Assert.NotNull(page.Find("[data-testid='top-consumers-empty']"));
    }

    [Fact]
    public void A_failed_load_renders_an_error()
    {
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Fail(
            ApiCallStatus.Unavailable,
            "Foundry Gate's API isn't reachable right now.");

        var page = RenderPage<Dashboard>();

        Assert.Contains("isn't reachable", page.Find("[data-testid='dashboard-error']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Shows_a_skeleton_until_the_first_load_returns()
    {
        Api.Gate = new TaskCompletionSource();

        var page = RenderPage<Dashboard>();

        Assert.NotNull(page.Find("[data-testid='dashboard-loading']"));

        Api.Gate.SetResult();
        page.WaitForElement("[data-testid='stat-total-users']");
    }

    [Fact]
    public void Refreshes_itself_on_the_interval()
    {
        // The real page waits a minute; the interval is a parameter purely so this test doesn't.
        var page = RenderPage<Dashboard>(("RefreshInterval", TimeSpan.FromMilliseconds(20)));

        page.WaitForAssertion(() => Assert.True(Api.CallCount("GetDashboardAsync") >= 3), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stops_refreshing_once_the_page_is_gone()
    {
        var page = RenderPage<Dashboard>(("RefreshInterval", TimeSpan.FromMilliseconds(20)));
        page.WaitForAssertion(() => Assert.True(Api.CallCount("GetDashboardAsync") >= 2), TimeSpan.FromSeconds(5));

        await DisposeComponentsAsync();
        var callsAtDisposal = Api.CallCount("GetDashboardAsync");

        // A timer that outlives its page keeps hitting an admin-only endpoint forever.
        await Task.Delay(200);
        Assert.True(
            Api.CallCount("GetDashboardAsync") <= callsAtDisposal + 1,
            $"Refresh loop kept running after disposal: {callsAtDisposal} calls at disposal, {Api.CallCount("GetDashboardAsync")} after.");
    }

    [Fact]
    public void A_background_refresh_failure_keeps_the_last_good_numbers_on_screen()
    {
        var page = RenderPage<Dashboard>(("RefreshInterval", TimeSpan.FromMilliseconds(20)));
        page.WaitForElement("[data-testid='stat-total-users']");

        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Fail(ApiCallStatus.Unavailable, "Gone.");
        page.WaitForAssertion(() => Assert.True(Api.CallCount("GetDashboardAsync") >= 3), TimeSpan.FromSeconds(5));

        Assert.NotNull(page.Find("[data-testid='stat-total-users']"));
        Assert.DoesNotContain(Snackbars, s => s.Message?.Contains("Gone.", StringComparison.Ordinal) == true);
    }
}
