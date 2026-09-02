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
    public void The_previous_run_is_shown_on_a_cold_load()
    {
        // #171: the page used to show only the run you triggered in this browser session, and nothing
        // at all on first load. The counts now come from configuration rows the sync writes itself,
        // so a run triggered from anywhere shows up here.
        Api.LastUserSyncResult = ApiCallResult<UserSyncStatusResponse>.Ok(new UserSyncStatusResponse(
            new DateTimeOffset(2026, 8, 31, 6, 30, 0, TimeSpan.Zero),
            new UserSyncResult(2, 40, 1, 0, 0)));

        var page = RenderPage<UsersSync>();

        var note = page.Find("[data-testid=sync-history-note]").TextContent;
        Assert.Contains("Last run", note, StringComparison.Ordinal);
        Assert.Contains("2 added, 40 updated, 1 deactivated, 0 failed.", note, StringComparison.Ordinal);
        Assert.Equal(0, Api.CallCount("SyncUsersAsync"));
    }

    [Fact]
    public void A_fork_that_has_never_synced_says_so_rather_than_showing_a_blank()
    {
        var page = RenderPage<UsersSync>();

        Assert.Contains(
            "no record of a previous sync",
            page.Find("[data-testid=sync-last-summary]").TextContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_recorded_run_whose_counts_could_not_be_read_still_shows_when_it_ran()
    {
        // Stored JSON that cannot be parsed reads as "no result" — a broken souvenir of a past run
        // must not take the timestamp down with it.
        Api.LastUserSyncResult = ApiCallResult<UserSyncStatusResponse>.Ok(
            new UserSyncStatusResponse(new DateTimeOffset(2026, 8, 31, 6, 30, 0, TimeSpan.Zero), null));

        var page = RenderPage<UsersSync>();

        var note = page.Find("[data-testid=sync-history-note]").TextContent;
        Assert.Contains("Last run", note, StringComparison.Ordinal);
        Assert.Contains("weren't recorded", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_status_read_says_so_rather_than_claiming_there_was_no_previous_run()
    {
        // "Couldn't read the last run" and "there was no last run" are different facts, and only one
        // of them is something the page actually learned.
        Api.LastUserSyncResult = ApiCallResult<UserSyncStatusResponse>.Fail(
            ApiCallStatus.Unavailable, "Foundry Gate's API isn't reachable right now.");

        var page = RenderPage<UsersSync>();

        var summary = page.Find("[data-testid=sync-last-summary]").TextContent;
        Assert.Contains("Couldn't read the last run", summary, StringComparison.Ordinal);
        Assert.Contains("isn't reachable", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("no record of a previous sync", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_status_read_does_not_stop_the_page_doing_its_job()
    {
        Api.LastUserSyncResult = ApiCallResult<UserSyncStatusResponse>.Fail(ApiCallStatus.Unavailable, "Gone.");

        var page = RenderPage<UsersSync>();

        Assert.NotNull(page.Find("[data-testid=run-sync]"));
        Assert.DoesNotContain(Snackbars, s => s.Message?.Contains("Gone.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Finishing_a_run_re_reads_the_stored_record()
    {
        // Re-read rather than assumed: what a reload would show is what the page shows.
        Api.UserSyncResult = ApiCallResult<UserSyncResult>.Ok(new UserSyncResult(1, 1, 0, 0, 0));

        var page = RenderPage<UsersSync>();
        RunSync(page);

        page.WaitForAssertion(() => Assert.Equal(2, Api.CallCount("GetLastUserSyncAsync")));
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
