using Bunit;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web.Pages;

/// <summary>
/// <c>/users/{id}</c> (#51): the four tabs render <c>UserDetailResponse</c>, every mutation is
/// gated by the confirmation dialog, and the budget editor writes a tier cap rather than a
/// free-form number (D-013).
/// </summary>
public class UserDetailPageTests : WebTestContext
{
    public UserDetailPageTests()
    {
        SignInAsAdmin();
        Api.ArrangeTiers(WebTestData.Tiers);
    }

    private IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> RenderDetail(UserDetailResponse detail)
    {
        Api.UserDetailResult = ApiCallResult<UserDetailResponse>.Ok(detail);
        return RenderPage<UserDetail>(("Id", detail.User.UserId));
    }

    [Fact]
    public void Renders_the_users_identity_and_status()
    {
        var page = RenderDetail(WebTestData.UserDetail(WebTestData.User(displayName: "Ada Lovelace", email: "ada@example.com")));

        page.WaitForAssertion(() =>
        {
            Assert.Equal("Ada Lovelace", page.Find("[data-testid=user-name]").TextContent.Trim());
            Assert.Equal("ada@example.com", page.Find("[data-testid=user-email]").TextContent.Trim());
            Assert.Contains("Active", page.Find("[data-testid=user-status]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Shows_the_resolved_level_and_tier_for_the_current_allocation()
    {
        var page = RenderDetail(WebTestData.UserDetail(
            allocation: WebTestData.Allocation(allocatedTokens: 25_000_000, level: QuotaLevelType.GroupMax)));

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Highest group", page.Find("[data-testid=user-quota-level]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Power", page.Find("[data-testid=user-quota-tier]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Says_so_when_no_allocation_has_been_resolved_this_month()
    {
        var page = RenderDetail(WebTestData.UserDetail(allocation: null) with { CurrentAllocation = null });

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=user-quota-none]")));
    }

    [Fact]
    public void A_404_from_the_api_says_the_user_is_gone_rather_than_rendering_an_empty_page()
    {
        Api.UserDetailResult = ApiCallResult<UserDetailResponse>.Fail(ApiCallStatus.NotFound, "That wasn't found.");

        var page = RenderPage<UserDetail>(("Id", 404));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=user-not-found]")));
    }

    [Fact]
    public void Cancelling_the_confirmation_leaves_the_account_untouched()
    {
        var page = RenderDetail(WebTestData.UserDetail(WebTestData.User(isActive: true)));
        page.WaitForAssertion(() => Assert.NotNull(page.Find(".mud-switch input[type=checkbox]")));

        ToggleActiveSwitch(page);

        page.WaitForElement("[data-testid=confirm-cancel]").Click();

        page.WaitForAssertion(() => Assert.Empty(Api.DeactivatedUserIds));
    }

    [Fact]
    public void Confirming_the_dialog_deactivates_the_user()
    {
        var page = RenderDetail(WebTestData.UserDetail(WebTestData.User(userId: 42, isActive: true)));
        page.WaitForAssertion(() => Assert.NotNull(page.Find(".mud-switch input[type=checkbox]")));

        ToggleActiveSwitch(page);
        page.WaitForElement("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() => Assert.Equal(42, Assert.Single(Api.DeactivatedUserIds)));
    }

    [Fact]
    public void Confirming_the_dialog_activates_a_deactivated_user()
    {
        var page = RenderDetail(WebTestData.UserDetail(WebTestData.User(userId: 42, isActive: false)));
        page.WaitForAssertion(() => Assert.NotNull(page.Find(".mud-switch input[type=checkbox]")));

        ToggleActiveSwitch(page);
        page.WaitForElement("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() => Assert.Equal(42, Assert.Single(Api.ActivatedUserIds)));
    }

    [Fact]
    public void Provisioning_a_key_is_offered_only_when_there_isnt_one_and_reveals_it_once()
    {
        var page = RenderDetail(WebTestData.UserDetail(
            WebTestData.User(userId: 7, isApiKeyProvisioned: false),
            apiKey: new ApiKeyResponse(false, null, null)));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=user-key-none]")));

        // MudTabs is KeepPanelsAlive, so every panel's content is in the DOM.
        page.Find("[data-testid=user-key-provision]").Click();
        page.WaitForElement("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Equal(7, Assert.Single(Api.ProvisionedKeyUserIds));
            Assert.Contains("fg-plaintext-key", page.Find("[data-testid=revealed-key]").GetAttribute("value"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Revoking_a_key_needs_confirmation_and_does_not_deactivate_the_account()
    {
        var page = RenderDetail(WebTestData.UserDetail(WebTestData.User(userId: 7, isApiKeyProvisioned: true)));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=user-key-revoke]")));

        page.Find("[data-testid=user-key-revoke]").Click();
        page.WaitForElement("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Equal(7, Assert.Single(Api.RevokedKeyUserIds));
            Assert.Empty(Api.DeactivatedUserIds);
        });
    }

    [Fact]
    public void Group_memberships_are_listed_with_a_link_to_the_group()
    {
        var page = RenderDetail(WebTestData.UserDetail(groups: [WebTestData.Membership(groupId: 7, name: "Platform")]));

        page.WaitForAssertion(() =>
        {
            var table = page.Find("[data-testid=user-groups-table]");
            Assert.Contains("Platform", table.TextContent, StringComparison.Ordinal);
            Assert.Contains("groups/7", page.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_user_in_no_groups_is_told_where_their_budget_comes_from()
    {
        var page = RenderDetail(WebTestData.UserDetail(groups: []));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=user-groups-empty]")));
    }

    /// <summary>MudSwitch renders a checkbox input; flipping it is what opens the confirmation.</summary>
    private static void ToggleActiveSwitch(IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> page)
    {
        var input = page.Find(".mud-switch input[type=checkbox]");
        input.Change(!input.HasAttribute("checked"));
    }
}
