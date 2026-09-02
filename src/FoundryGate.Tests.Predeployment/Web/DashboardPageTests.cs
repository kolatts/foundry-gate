using Bunit;
using FoundryGate.Domain.Constants;
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
            totalTokensUsed: 987_654_321,
            hardStoppedUserCount: 2));

        var page = RenderPage<Dashboard>();

        Assert.Contains("120", page.Find("[data-testid='stat-total-users']").TextContent, StringComparison.Ordinal);
        Assert.Contains("118", page.Find("[data-testid='stat-active-users']").TextContent, StringComparison.Ordinal);
        Assert.Contains("4 unlimited", page.Find("[data-testid='stat-active-users']").TextContent, StringComparison.Ordinal);
        Assert.Contains("3", page.Find("[data-testid='stat-pending-requests']").TextContent, StringComparison.Ordinal);
        Assert.Contains("2", page.Find("[data-testid='stat-hard-stopped']").TextContent, StringComparison.Ordinal);
        Assert.Contains("987,654,321", page.Find("[data-testid='stat-tokens-used']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fourth_card_is_hard_stopped_users_not_the_tokens_substitute()
    {
        // #54 asked for a hard-stopped card; #174 shipped "Tokens this period" in the slot because
        // the summary carried no such figure (#190). Tokens stays on the page — next to the
        // consumers list it describes — but the card is the one #54 named.
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(hardStoppedUserCount: 3));

        var page = RenderPage<Dashboard>();

        var card = page.Find("[data-testid='stat-hard-stopped']").TextContent;
        Assert.Contains("Hard-stopped", card, StringComparison.Ordinal);
        Assert.Contains("3", card, StringComparison.Ordinal);
        Assert.Contains("no gateway key", card, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_zero_hard_stopped_count_reads_as_an_alert_and_links_to_the_pipeline_that_set_it()
    {
        // A hard stop is an outage for that developer, and the audit log is the only place that says
        // who and when — so the number is a link into it, not a dead figure. It has to be the action
        // that actually sets the flag: `user.deactivated` (whose details carry
        // `allocationHardStopped`). `key.revoked` is also written by DELETE /keys/{userId}, which
        // explicitly leaves the allocation alone, so linking there lands on mostly other people.
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(hardStoppedUserCount: 1));

        var page = RenderPage<Dashboard>();

        var link = page.Find("[data-testid='stat-hard-stopped-link']");
        Assert.Equal($"audit?action={AuditActions.UserDeactivated}", link.GetAttribute("href"));
        Assert.Contains("mud-error-text", page.Find("[data-testid='stat-hard-stopped']").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void Nobody_hard_stopped_says_so_without_alarming_anyone()
    {
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(hardStoppedUserCount: 0));

        var page = RenderPage<Dashboard>();

        Assert.Contains("Nobody is cut off", page.Find("[data-testid='stat-hard-stopped']").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='stat-hard-stopped-link']"));
    }

    [Fact]
    public void Over_budget_users_are_reported_next_to_the_usage_they_describe()
    {
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(overBudgetUserCount: 7));

        var page = RenderPage<Dashboard>();

        Assert.Contains("7 over budget", page.Find("[data-testid='stat-over-budget']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Nobody_over_budget_says_so()
    {
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(overBudgetUserCount: 0));

        var page = RenderPage<Dashboard>();

        Assert.Contains("nobody over budget", page.Find("[data-testid='stat-over-budget']").TextContent, StringComparison.Ordinal);
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

        // A timer that outlives its page keeps hitting an admin-only endpoint forever. Asserting
        // that by sampling the count once and again a fixed delay later made this test a race with
        // the thread pool (#195): under load a tick already in flight lands inside the window and
        // the count moves for a reason that is not the bug. Waiting for the count to stop moving,
        // and only then requiring it to stay still, tests "the loop stopped" rather than "the loop
        // was slower than 200 ms".
        var settled = await SettledCallCountAsync("GetDashboardAsync");

        await Task.Delay(200);
        Assert.Equal(settled, Api.CallCount("GetDashboardAsync"));
    }

    /// <summary>
    /// Polls until the named call count is the same twice running, and answers that count. Fails
    /// the test rather than looping forever if it never settles — a count that never stops moving
    /// is the bug this is here to catch.
    /// </summary>
    private async Task<int> SettledCallCountAsync(string method)
    {
        var previous = -1;

        for (var attempt = 0; attempt < 40; attempt++)
        {
            var current = Api.CallCount(method);
            if (current == previous)
            {
                return current;
            }

            previous = current;
            await Task.Delay(50);
        }

        Assert.Fail($"{method} never stopped being called after the page was disposed.");
        return previous;
    }

    [Fact]
    public async Task A_reply_that_lands_after_disposal_is_not_published_to_the_nav_badge()
    {
        // Dispose cancels the timer, but the GET that was already in flight still completes. Its
        // result must not reach DashboardState: that feeds the nav badge, which outlives the page.
        // (A long interval keeps the refresh loop out of this test — the loop's own shutdown is
        // covered by Stops_refreshing_once_the_page_is_gone.)
        Api.Gate = new TaskCompletionSource();
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(pendingRequestCount: 9));

        var page = RenderPage<Dashboard>(("RefreshInterval", TimeSpan.FromMinutes(5)));
        var dashboard = page.FindComponent<Dashboard>();

        await page.InvokeAsync(dashboard.Instance.Dispose);
        Api.Gate.SetResult();
        await Task.Delay(50);

        Assert.Equal(0, Services.GetRequiredService<DashboardStateService>().PendingRequestCount);
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
