using Bunit;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web.Pages;

/// <summary>
/// <c>/groups/{id}</c> (#52): the roster editor is present on a manual group and absent —
/// with an explanation — on an Entra-linked one, deletions are confirmed with the right
/// consequence, and the Entra sync result is shown rather than swallowed.
/// </summary>
public class GroupDetailPageTests : WebTestContext
{
    private const string EntraGroupId = "9f0d3c5a-0000-0000-0000-000000000001";

    public GroupDetailPageTests()
    {
        SignInAsAdmin();
        Api.ArrangeTiers(WebTestData.Tiers());
    }

    private IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> RenderDetail(GroupResponse group, params GroupMemberResponse[] members)
    {
        Api.ArrangeGroup(group, members);
        return RenderPage<GroupDetail>(("Id", group.GroupId));
    }

    [Fact]
    public void Renders_the_group_and_its_roster()
    {
        var page = RenderDetail(
            AdminTestData.Group(groupId: 7, name: "Platform"),
            AdminTestData.Member(userId: 1, displayName: "Ada Lovelace"),
            AdminTestData.Member(userId: 2, displayName: "Grace Hopper"));

        page.WaitForAssertion(() =>
        {
            Assert.Equal("Platform", page.Find("[data-testid=group-name]").TextContent.Trim());
            Assert.Contains("Ada Lovelace", page.Markup, StringComparison.Ordinal);
            Assert.Contains("Grace Hopper", page.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_manual_group_offers_the_add_and_remove_controls()
    {
        var page = RenderDetail(AdminTestData.Group(groupId: 7), AdminTestData.Member(userId: 1));

        page.WaitForAssertion(() =>
        {
            Assert.NotNull(page.Find("[data-testid=group-add-member]"));
            Assert.NotNull(page.Find("[data-testid=group-remove-1]"));
        });
    }

    [Fact]
    public void An_entra_linked_group_hides_the_roster_controls_and_says_why()
    {
        // The API answers 409 for a membership edit on a linked group, because the next sync
        // would undo it. Offering the button and then failing would be worse than not offering it.
        var page = RenderDetail(AdminTestData.Group(groupId: 7, entraGroupId: EntraGroupId), AdminTestData.Member(userId: 1));

        page.WaitForAssertion(() =>
        {
            Assert.Contains("owned by Entra", page.Find("[data-testid=group-entra-notice]").TextContent, StringComparison.Ordinal);
            Assert.Empty(page.FindAll("[data-testid=group-add-member]"));
            Assert.Empty(page.FindAll("[data-testid=group-remove-1]"));
        });
    }

    [Fact]
    public void Only_an_entra_linked_group_offers_a_sync_button()
    {
        var manual = RenderDetail(AdminTestData.Group(groupId: 7));
        manual.WaitForAssertion(() => Assert.NotNull(manual.Find("[data-testid=group-save]")));
        Assert.Empty(manual.FindAll("[data-testid=group-sync]"));
    }

    [Fact]
    public void Syncing_from_entra_shows_the_added_removed_and_skipped_counts()
    {
        Api.GroupSyncResult = ApiCallResult<GroupSyncResult>.Ok(new GroupSyncResult(7, AddedCount: 2, RemovedCount: 1, SkippedUnknownUserCount: 3));

        var page = RenderDetail(AdminTestData.Group(groupId: 7, entraGroupId: EntraGroupId));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=group-sync]")));

        page.Find("[data-testid=group-sync]").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Equal(7, Assert.Single(Api.EntraSyncedGroupIds));
            Assert.Equal("2", page.Find("[data-testid=sync-added]").TextContent.Trim());
            Assert.Equal("1", page.Find("[data-testid=sync-removed]").TextContent.Trim());
            Assert.Equal("3", page.Find("[data-testid=sync-skipped]").TextContent.Trim());
        });
    }

    [Fact]
    public void Removing_a_member_is_gated_by_the_confirmation()
    {
        var page = RenderDetail(AdminTestData.Group(groupId: 7), AdminTestData.Member(userId: 1));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=group-remove-1]")));

        page.Find("[data-testid=group-remove-1]").Click();
        page.WaitForElement("[data-testid=confirm-cancel]").Click();

        page.WaitForAssertion(() => Assert.Empty(Api.RemovedGroupMembers));
    }

    [Fact]
    public void Confirming_removes_the_member()
    {
        var page = RenderDetail(AdminTestData.Group(groupId: 7), AdminTestData.Member(userId: 1));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=group-remove-1]")));

        page.Find("[data-testid=group-remove-1]").Click();
        page.WaitForElement("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() => Assert.Equal((7, 1), Assert.Single(Api.RemovedGroupMembers)));
    }

    [Fact]
    public void Deleting_a_group_with_members_forces_and_says_what_that_costs()
    {
        var page = RenderDetail(AdminTestData.Group(groupId: 7, memberCount: 4), AdminTestData.Member(userId: 1));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=group-delete]")));

        page.Find("[data-testid=group-delete]").Click();
        var dialog = page.WaitForElement("[data-testid=confirm-ok]");

        Assert.Contains("4 member", page.Markup, StringComparison.Ordinal);
        dialog.Click();

        page.WaitForAssertion(() => Assert.Equal((7, true), Assert.Single(Api.DeletedGroups)));
    }

    [Fact]
    public void Deleting_an_empty_group_does_not_force()
    {
        var page = RenderDetail(AdminTestData.Group(groupId: 7, memberCount: 0));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=group-delete]")));

        page.Find("[data-testid=group-delete]").Click();
        page.WaitForElement("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() => Assert.Equal((7, false), Assert.Single(Api.DeletedGroups)));
    }

    [Fact]
    public void A_404_from_the_api_says_the_group_is_gone()
    {
        Api.GroupDetailResult = ApiCallResult<GroupDetailResponse>.Fail(ApiCallStatus.NotFound, "That wasn't found.");

        var page = RenderPage<GroupDetail>(("Id", 404));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=group-not-found]")));
    }
}
