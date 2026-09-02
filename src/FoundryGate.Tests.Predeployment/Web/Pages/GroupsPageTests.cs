using Bunit;
using FoundryGate.Domain.Common;
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
    public GroupsPageTests()
    {
        SignInAsAdmin();
        Api.ArrangeTiers(WebTestData.Tiers());
    }

    [Fact]
    public void Renders_member_count_budget_tier_and_the_roster_source()
    {
        Api.ArrangeGroups(
            AdminTestData.Group(groupId: 1, name: "Platform", memberCount: 4, monthlyTokenQuota: 20_000_000),
            AdminTestData.Group(groupId: 2, name: "Data", entraGroupId: "9f0d3c5a-0000-0000-0000-000000000001"));

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

        page.Find("input[data-testid=groups-search]").Input("platform");

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
    public void The_create_form_sends_what_was_typed()
    {
        Api.CreateGroupResult = ApiCallResult<GroupResponse>.Ok(AdminTestData.Group(groupId: 9, name: "Platform"));

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
