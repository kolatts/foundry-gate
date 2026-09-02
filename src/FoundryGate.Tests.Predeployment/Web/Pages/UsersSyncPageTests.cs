using Bunit;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web.Pages;

/// <summary>
/// <c>/users/sync</c> (#63): the button runs <c>POST /users/sync</c> once and the page explains
/// the two counts an admin can't act on from the number alone — suspended departure detection
/// (#121) and a failed deprovision, which means someone still holds a working key.
/// </summary>
public class UsersSyncPageTests : WebTestContext
{
    public UsersSyncPageTests() => SignInAsAdmin();

    [Fact]
    public void Nothing_is_called_until_the_button_is_pressed()
    {
        RenderPage<UsersSync>();

        Assert.Equal(0, Api.CallCount("SyncUsersAsync"));
    }

    [Fact]
    public void Cancelling_the_confirmation_runs_nothing()
    {
        // This button deactivates accounts and deletes APIM subscriptions in bulk; it was the one
        // unconfirmed action in the admin surface.
        var page = RenderPage<UsersSync>();

        page.Find("[data-testid=run-sync]").Click();
        page.WaitForElement("[data-testid=confirm-cancel]").Click();

        page.WaitForAssertion(() => Assert.Equal(0, Api.CallCount("SyncUsersAsync")));
    }

    [Fact]
    public void Running_the_sync_reports_every_count()
    {
        Api.UserSyncResult = ApiCallResult<UserSyncResult>.Ok(new UserSyncResult(3, 12, 1, 0, 0));

        var page = RenderPage<UsersSync>();
        RunSync(page);

        page.WaitForAssertion(() =>
        {
            Assert.Equal(1, Api.CallCount("SyncUsersAsync"));
            Assert.Contains("3 added", page.Find("[data-testid=sync-added]").TextContent, StringComparison.Ordinal);
            Assert.Contains("12 updated", page.Find("[data-testid=sync-updated]").TextContent, StringComparison.Ordinal);
            Assert.Contains("1 deactivated", page.Find("[data-testid=sync-deactivated]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_skipped_group_assignment_explains_why_nobody_was_deactivated()
    {
        Api.UserSyncResult = ApiCallResult<UserSyncResult>.Ok(new UserSyncResult(1, 4, 0, 2, 0));

        var page = RenderPage<UsersSync>();
        RunSync(page);

        page.WaitForAssertion(() =>
        {
            var explainer = page.Find("[data-testid=sync-skipped-explainer]");
            Assert.Contains("Nobody was deactivated", explainer.TextContent, StringComparison.Ordinal);

            // #186 inverted this count: expansion works, so a non-zero value is the groups whose
            // expansion FAILED. The copy has to name the fix (the Graph role), not tell an admin to
            // restructure their tenant.
            Assert.Contains("could not be expanded", explainer.TextContent, StringComparison.Ordinal);
            Assert.Contains("GroupMember.ReadBasic.All", explainer.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("individually", explainer.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_failed_deprovision_is_reported_as_someone_still_holding_a_key()
    {
        Api.UserSyncResult = ApiCallResult<UserSyncResult>.Ok(new UserSyncResult(0, 4, 2, 0, 1));

        var page = RenderPage<UsersSync>();
        RunSync(page);

        page.WaitForAssertion(() =>
            Assert.Contains("still hold a working key", page.Find("[data-testid=sync-failed-explainer]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void A_clean_run_shows_neither_explainer()
    {
        Api.UserSyncResult = ApiCallResult<UserSyncResult>.Ok(new UserSyncResult(1, 4, 1, 0, 0));

        var page = RenderPage<UsersSync>();
        RunSync(page);

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=sync-result]")));
        Assert.Empty(page.FindAll("[data-testid=sync-skipped-explainer]"));
        Assert.Empty(page.FindAll("[data-testid=sync-failed-explainer]"));
    }

    /// <summary>Presses the button and confirms — the sync is gated on a dialog.</summary>
    private static void RunSync(IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> page)
    {
        page.Find("[data-testid=run-sync]").Click();
        page.WaitForElement("[data-testid=confirm-ok]").Click();
    }

    [Fact]
    public void The_page_admits_it_cannot_show_the_previous_run()
    {
        // There are no LastUserSync* configuration keys — #63 assumed them. Until #171 lands the
        // page has to say so rather than imply the blank space means "never run".
        var page = RenderPage<UsersSync>();

        Assert.Contains("171", page.Find("[data-testid=sync-history-note]").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_403_from_the_api_renders_access_denied()
    {
        Api.UserSyncResult = ApiCallResult<UserSyncResult>.Fail(ApiCallStatus.Forbidden, "You don't have permission to do that.");

        var page = RenderPage<UsersSync>();
        RunSync(page);

        page.WaitForAssertion(() => Assert.Contains("Access denied", page.Markup, StringComparison.Ordinal));
    }
}
