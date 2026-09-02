using Bunit;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Groups;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web.Pages;

/// <summary>
/// <c>/groups</c> and <c>/groups/new</c> (#52): the list distinguishes an Entra-managed roster
/// from a manual one, and the create form builds a <see cref="CreateGroupRequest"/> from its
/// fields, validating the optional Entra link before it reaches the API.
/// </summary>
public class GroupsPageTests : WebTestContext
{
    private const string EntraGroupId = "9f0d3c5a-0000-0000-0000-000000000001";

    public GroupsPageTests()
    {
        SignInAsAdmin();
        Api.ArrangeTiers(WebTestData.Tiers);
    }

    [Fact]
    public void Renders_member_count_budget_tier_and_the_roster_source()
    {
        Api.ArrangeGroups(
            WebTestData.Group(groupId: 1, name: "Platform", memberCount: 4, monthlyTokenQuota: 20_000_000),
            WebTestData.Group(groupId: 2, name: "Data", entraGroupId: "9f0d3c5a-0000-0000-0000-000000000001"));

        var page = RenderPage<Groups>();

        page.WaitForAssertion(() =>
        {
            var markup = page.Markup;
            Assert.Contains("Platform", markup, StringComparison.Ordinal);
            Assert.Contains("Power", markup, StringComparison.Ordinal);
            Assert.Contains("Manual", markup, StringComparison.Ordinal);
            Assert.Contains("Entra", markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Searching_re_queries_from_page_one()
    {
        var page = RenderPage<Groups>();
        page.WaitForAssertion(() => Assert.NotEmpty(Api.GroupListCalls));

        // The search box debounces for 300 ms before it re-queries, so the second ServerData call
        // is what this test is actually waiting for — assert on the count first and only then on
        // its contents, so a timeout says "the page never re-queried" rather than "no call
        // matched", and so the assertion never reads a half-appended list (#203).
        var before = Api.GroupListCalls.Count;

        page.Find("input[data-testid=groups-search]").Input("platform");

        page.WaitForAssertion(() => Assert.True(
            Api.GroupListCalls.Count > before,
            $"the grid should have re-queried after the search input; it has made {Api.GroupListCalls.Count} call(s) in total."));

        page.WaitForAssertion(() =>
        {
            var searched = Api.GroupListCalls.Where(c => c.Search == "platform").ToList();
            Assert.NotEmpty(searched);
            Assert.All(searched, c => Assert.Equal(1, c.Paging.Page));
        });
    }

    [Fact]
    public void An_empty_list_invites_creating_the_first_group()
    {
        var page = RenderPage<Groups>();

        page.WaitForAssertion(() =>
            Assert.Contains("Create one", page.Find("[data-testid=groups-empty]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void A_403_from_the_api_renders_access_denied()
    {
        Api.GroupsResult = ApiCallResult<PagedResult<GroupResponse>>.Fail(ApiCallStatus.Forbidden, "You don't have permission to do that.");

        var page = RenderPage<Groups>();

        page.WaitForAssertion(() => Assert.Contains("Access denied", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void Cancelling_the_bulk_sync_confirmation_calls_nothing()
    {
        var page = RenderPage<Groups>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=groups-sync-all]")));

        page.Find("[data-testid=groups-sync-all]").Click();
        page.WaitForElement("[data-testid=confirm-cancel]").Click();

        page.WaitForAssertion(() => Assert.Equal(0, Api.CallCount("SyncGroupsFromEntraAsync")));
    }

    [Fact]
    public void The_bulk_sync_summary_separates_a_failed_group_from_a_reconciled_one()
    {
        // POST /groups/sync-entra keeps going past a failure, so the run is a 200 carrying
        // per-group Succeeded flags (#186). A dialog that rendered every row alike would hide the
        // only rows worth reading.
        Api.ArrangeGroups(WebTestData.Group(groupId: 1, name: "Platform", entraGroupId: EntraGroupId));
        Api.GroupsSyncResult = ApiCallResult<IReadOnlyList<GroupSyncResult>>.Ok(
        [
            new GroupSyncResult(1, AddedCount: 2, RemovedCount: 1, SkippedUnknownUserCount: 0),
            new GroupSyncResult(2, 0, 0, 0, Succeeded: false, Error: "Graph refused the read.", ErrorType: GroupSyncErrorType.GraphRead),
            new GroupSyncResult(3, 0, 0, 0, Succeeded: false, Error: "Save failed after the tier moved.", ErrorType: GroupSyncErrorType.PostCommit),
        ]);

        var page = RenderPage<Groups>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=groups-sync-all]")));
        page.Find("[data-testid=groups-sync-all]").Click();
        page.WaitForElement("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Equal(1, Api.CallCount("SyncGroupsFromEntraAsync"));
            Assert.Contains("1 of 3", page.Find("[data-testid=sync-all-summary]").TextContent, StringComparison.Ordinal);

            // The two failures read differently, because one is "re-run it" and the other is
            // "someone has to look".
            Assert.Contains("nothing was changed", page.Find("[data-testid=sync-all-error-2]").TextContent, StringComparison.Ordinal);
            Assert.Contains("needs checking", page.Find("[data-testid=sync-all-error-3]").TextContent, StringComparison.Ordinal);
            Assert.NotNull(page.Find("[data-testid=sync-all-graph-read]"));
            Assert.NotNull(page.Find("[data-testid=sync-all-post-commit]"));

            // The succeeded group still shows its name rather than a bare id.
            Assert.Contains("Platform", page.Find("[data-testid=sync-all-row-1]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Every_row_carries_a_control_a_keyboard_can_reach()
    {
        Api.ArrangeGroups(WebTestData.Group(groupId: 1), WebTestData.Group(groupId: 2, name: "Data"));

        var page = RenderPage<Groups>();

        page.WaitForAssertion(() =>
        {
            Assert.Equal("groups/1", page.Find("[data-testid=groups-open-1]").GetAttribute("href"));
            Assert.Equal("groups/2", page.Find("[data-testid=groups-open-2]").GetAttribute("href"));
        });
    }

    [Fact]
    public void The_create_form_sends_what_was_typed()
    {
        Api.CreateGroupResult = ApiCallResult<GroupResponse>.Ok(WebTestData.Group(groupId: 9, name: "Platform"));

        var page = RenderPage<GroupNew>();
        page.Find("input[data-testid=group-name]").Input("Platform");
        page.Find("[data-testid=group-create]").Click();

        page.WaitForAssertion(() =>
        {
            var created = Assert.Single(Api.CreatedGroups);
            Assert.Equal("Platform", created.Name);
            Assert.Null(created.EntraGroupId);
        });
    }

    [Fact]
    public void An_entra_group_id_that_isnt_a_guid_never_reaches_the_api()
    {
        var page = RenderPage<GroupNew>();
        page.Find("input[data-testid=group-name]").Input("Platform");
        page.Find("input[data-testid=group-entra-id]").Change("the-data-team");
        page.Find("[data-testid=group-create]").Click();

        page.WaitForAssertion(() => Assert.Empty(Api.CreatedGroups));
    }

    [Fact]
    public void A_nameless_group_never_reaches_the_api()
    {
        var page = RenderPage<GroupNew>();
        page.Find("[data-testid=group-create]").Click();

        page.WaitForAssertion(() => Assert.Empty(Api.CreatedGroups));
    }
}
