using Bunit;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;
using MudBlazor;

namespace FoundryGate.Tests.Predeployment.Web.Pages;

/// <summary>
/// <c>/quota</c> (#208): the list the dashboard's hard-stopped and over-budget counts link to.
/// What matters here is that the page's filters are the API's filters — a card that says 2 and a
/// page that shows 9 is worse than no link at all — so most of these assert on the
/// <see cref="QuotaAllocationQuery"/> the grid actually sent.
/// </summary>
public class QuotaAllocationsPageTests : WebTestContext
{
    public QuotaAllocationsPageTests()
    {
        SignInAsAdmin();
        Api.ArrangeTiers(WebTestData.Tiers);
    }

    [Fact]
    public void Renders_the_developer_the_tier_and_the_usage_for_each_allocation()
    {
        Api.ArrangeAllocations(WebTestData.Allocation(
            userId: 11,
            allocatedTokens: 20_000_000,
            tokensUsed: 4_000_000,
            percentUsed: 20,
            tierProductId: GatewayTiers.Power,
            userDisplayName: "Grace Hopper",
            userEmail: "grace@contoso.test"));

        var page = RenderPage<QuotaAllocations>();

        page.WaitForAssertion(() =>
        {
            var grid = page.Find("[data-testid=quota-grid]").TextContent;
            Assert.Contains("Grace Hopper", grid, StringComparison.Ordinal);
            Assert.Contains("grace@contoso.test", grid, StringComparison.Ordinal);
            Assert.Contains("Power", grid, StringComparison.Ordinal);
            Assert.Contains("4,000,000", grid, StringComparison.Ordinal);
            Assert.Contains("20,000,000", grid, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void An_unfiltered_visit_asks_for_everything()
    {
        var page = RenderPage<QuotaAllocations>();

        page.WaitForAssertion(() =>
        {
            var query = Assert.Single(Api.QuotaAllocationListCalls).Query;
            Assert.Null(query.IsHardStopped);
            Assert.Null(query.IsOverBudget);
            Assert.Null(query.IsActive);
            Assert.Null(query.Tier);
            Assert.Null(query.Search);
        });
    }

    [Fact]
    public void The_dashboards_hard_stopped_link_arrives_as_a_filtered_query()
    {
        // The href the dashboard renders, arriving as route query parameters. Both halves have to
        // reach the API: the count is scoped to active users, so the list must be too.
        var page = RenderPage<QuotaAllocations>(("HardStoppedQuery", true), ("ActiveQuery", true));

        page.WaitForAssertion(() =>
        {
            var query = Api.QuotaAllocationListCalls[^1].Query;
            Assert.True(query.IsHardStopped);
            Assert.True(query.IsActive);
            Assert.Null(query.IsOverBudget);
        });
    }

    [Fact]
    public void The_dashboards_over_budget_link_arrives_as_a_filtered_query()
    {
        var page = RenderPage<QuotaAllocations>(("OverBudgetQuery", true), ("ActiveQuery", true));

        page.WaitForAssertion(() =>
        {
            var query = Api.QuotaAllocationListCalls[^1].Query;
            Assert.True(query.IsOverBudget);
            Assert.True(query.IsActive);
            Assert.Null(query.IsHardStopped);
        });
    }

    [Fact]
    public void A_chip_toggles_its_filter_on_and_back_off()
    {
        var page = RenderPage<QuotaAllocations>();
        page.WaitForAssertion(() => Assert.NotEmpty(Api.QuotaAllocationListCalls));

        page.Find("[data-testid=quota-chip-hard-stopped]").Click();
        page.WaitForAssertion(() => Assert.True(Api.QuotaAllocationListCalls[^1].Query.IsHardStopped));

        // Off means "no opinion", not "only the ones that are not hard-stopped" — a chip that
        // silently inverted itself would hide the rows an admin came here for.
        page.Find("[data-testid=quota-chip-hard-stopped]").Click();
        page.WaitForAssertion(() => Assert.Null(Api.QuotaAllocationListCalls[^1].Query.IsHardStopped));
    }

    [Fact]
    public void Searching_re_queries_from_page_one()
    {
        var page = RenderPage<QuotaAllocations>();
        page.WaitForAssertion(() => Assert.NotEmpty(Api.QuotaAllocationListCalls));
        var before = Api.QuotaAllocationListCalls.Count;

        page.Find("input[data-testid=quota-search]").Input("hopper");

        page.WaitForAssertion(() => Assert.True(
            Api.QuotaAllocationListCalls.Count > before,
            "the grid should have re-queried after the search input."));
        page.WaitForAssertion(() =>
        {
            var searched = Api.QuotaAllocationListCalls.Where(c => c.Query.Search == "hopper").ToList();
            Assert.NotEmpty(searched);
            Assert.All(searched, c => Assert.Equal(1, c.Paging.Page));
        });
    }

    [Fact]
    public void Clearing_the_filters_asks_for_everything_again()
    {
        var page = RenderPage<QuotaAllocations>(("HardStoppedQuery", true), ("ActiveQuery", true));
        page.WaitForAssertion(() => Assert.NotEmpty(Api.QuotaAllocationListCalls));

        page.WaitForElement("[data-testid=quota-chip-clear]").Click();

        page.WaitForAssertion(() =>
        {
            var query = Api.QuotaAllocationListCalls[^1].Query;
            Assert.Null(query.IsHardStopped);
            Assert.Null(query.IsActive);
        });
    }

    [Fact]
    public void A_hard_stopped_row_says_so_and_so_does_one_that_has_spent_its_budget()
    {
        // The over-budget chip is rendered from the same >= rule the API filters and the dashboard
        // counts with: at the cap is already cut off, because the gateway refuses the request that
        // would cross it.
        Api.ArrangeAllocations(
            WebTestData.Allocation(userId: 11, allocatedTokens: 5_000_000, tokensUsed: 5_000_000, percentUsed: 100, isHardStopped: true),
            WebTestData.Allocation(userId: 12, allocatedTokens: 5_000_000, tokensUsed: 4_999_999, percentUsed: 99));

        var page = RenderPage<QuotaAllocations>();

        page.WaitForAssertion(() =>
        {
            Assert.NotNull(page.Find("[data-testid=quota-hard-stopped-11]"));
            Assert.NotNull(page.Find("[data-testid=quota-over-budget-11]"));
            Assert.Empty(page.FindAll("[data-testid=quota-over-budget-12]"));
        });
    }

    [Fact]
    public void An_unlimited_allocation_shows_a_chip_rather_than_a_bar()
    {
        Api.ArrangeAllocations(WebTestData.Allocation(userId: 13, isUnlimited: true, tierProductId: GatewayTiers.Unlimited));

        var page = RenderPage<QuotaAllocations>();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Unlimited", page.Find("[data-testid=quota-grid]").TextContent, StringComparison.Ordinal);
            Assert.Empty(page.FindAll("[data-testid=quota-over-budget-13]"));
        });
    }

    [Fact]
    public void An_estimated_cost_column_appears_once_the_fork_has_priced_its_tokens()
    {
        Api.ArrangeAllocations(WebTestData.Allocation(userId: 11, tokensUsed: 4_000_000, estimatedCost: 36m));

        var page = RenderPage<QuotaAllocations>();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("36.00", page.Find("[data-testid=quota-cost-11]").TextContent, StringComparison.Ordinal);

            // #177: an unqualified currency figure next to a developer's name reads as a bill. The
            // caveat is a tooltip, which MudBlazor renders on hover rather than into the markup.
            Assert.Contains(
                page.FindComponents<MudTooltip>(),
                tooltip => tooltip.Instance.Text?.Contains("#177", StringComparison.Ordinal) == true);
        });
    }

    [Fact]
    public void Without_a_rate_card_there_is_no_cost_column_at_all()
    {
        // An always-empty "Est. cost" column reads as "we could not work it out" rather than as
        // "nobody has priced this fork's tokens".
        Api.ArrangeAllocations(WebTestData.Allocation(userId: 11, estimatedCost: null));

        var page = RenderPage<QuotaAllocations>();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=quota-grid]")));
        Assert.Empty(page.FindAll("[data-testid=quota-cost-11]"));
        Assert.DoesNotContain("Est. cost", page.Find("[data-testid=quota-grid]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_row_offers_a_keyboard_reachable_way_into_the_user()
    {
        Api.ArrangeAllocations(WebTestData.Allocation(userId: 11));

        var page = RenderPage<QuotaAllocations>();

        page.WaitForAssertion(() =>
            Assert.Equal("users/11", page.Find("[data-testid=quota-open-11]").GetAttribute("href")));
    }

    [Fact]
    public void An_empty_period_explains_itself_rather_than_looking_broken()
    {
        var page = RenderPage<QuotaAllocations>();

        page.WaitForAssertion(() =>
            Assert.Contains("No allocations for this period yet", page.Find("[data-testid=quota-empty]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_filtered_list_says_it_is_the_filter_not_the_period()
    {
        var page = RenderPage<QuotaAllocations>(("HardStoppedQuery", true));

        page.WaitForAssertion(() =>
            Assert.Contains("No allocations match those filters", page.Find("[data-testid=quota-empty]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void A_403_from_the_api_renders_access_denied()
    {
        Api.QuotaAllocationsResult = ApiCallResult<PagedResult<QuotaAllocationResponse>>.Fail(
            ApiCallStatus.Forbidden,
            "You don't have permission to do that.");

        var page = RenderPage<QuotaAllocations>();

        page.WaitForAssertion(() => Assert.Contains("Access denied", page.Markup, StringComparison.Ordinal));
    }
}
