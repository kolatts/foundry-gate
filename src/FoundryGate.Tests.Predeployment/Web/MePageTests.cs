using Bunit;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// <c>/me</c> (#49): the gauge's states, the key panel's three states, the CLI snippets the docs
/// promise, and the request history.
/// </summary>
public class MePageTests : WebTestContext
{
    public MePageTests() => SignInAsDeveloper();

    [Fact]
    public void Renders_the_gauge_the_key_and_the_cli_panel_from_one_profile_call()
    {
        var page = RenderPage<Me>();

        Assert.NotNull(page.Find("[data-testid='quota-gauge']"));
        Assert.NotNull(page.Find("[data-testid='key-display']"));
        Assert.NotNull(page.Find("[data-testid='cli-setup']"));

        // GET /users/me carries quota + key + gateway addressing together: one call, not three.
        Assert.Equal(1, Api.CallCount("GetMeAsync"));
        Assert.Equal(0, Api.CallCount("GetMyQuotaAllocationAsync"));
        Assert.Equal(0, Api.CallCount("GetMyKeyAsync"));
    }

    [Fact]
    public void Shows_token_counts_and_the_enforced_tier()
    {
        Api.MeResult = Ok(WebTestData.Profile(
            quota: WebTestData.Allocation(allocatedTokens: 5_000_000, tokensUsed: 1_250_000, percentUsed: 25)));

        var page = RenderPage<Me>();

        var numbers = page.Find("[data-testid='quota-numbers']").TextContent;
        Assert.Contains("1,250,000", numbers, StringComparison.Ordinal);
        Assert.Contains("5,000,000", numbers, StringComparison.Ordinal);
        Assert.Contains("Standard", page.Find("[data-testid='quota-tier-chip']").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, "success")]
    [InlineData(85, "warning")]
    [InlineData(99, "error")]
    public void Colours_the_bar_by_how_much_of_the_budget_is_gone(double percentUsed, string expectedColorClass)
    {
        Api.MeResult = Ok(WebTestData.Profile(quota: WebTestData.Allocation(percentUsed: percentUsed)));

        var page = RenderPage<Me>();

        var bar = page.Find("[data-testid='quota-bar']");
        Assert.Contains($"mud-progress-linear-color-{expectedColorClass}", bar.GetAttribute("class") ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlimited_developer_gets_a_chip_not_a_bar()
    {
        Api.MeResult = Ok(WebTestData.Profile(
            quota: WebTestData.Allocation(isUnlimited: true, tierProductId: "unlimited")));

        var page = RenderPage<Me>();

        Assert.NotNull(page.Find("[data-testid='quota-unlimited']"));
        Assert.Empty(page.FindAll("[data-testid='quota-bar']"));
    }

    [Fact]
    public void Warns_when_the_number_shown_is_not_the_number_the_gateway_enforces()
    {
        Api.MeResult = Ok(WebTestData.Profile(
            quota: WebTestData.Allocation(allocatedTokens: 7_000_000, isGatewayCapped: true, tierProductId: "power")));

        var page = RenderPage<Me>();

        Assert.Contains("next tier up", page.Find("[data-testid='quota-gateway-capped']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Explains_which_level_of_the_precedence_chain_produced_the_budget()
    {
        Api.MeResult = Ok(WebTestData.Profile(
            quota: WebTestData.Allocation(level: QuotaLevelType.GroupMax)));

        var page = RenderPage<Me>();

        Assert.Contains("group", page.Find("[data-testid='quota-gauge']").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unprovisioned_key_gets_a_friendly_state_not_an_error()
    {
        Api.MeResult = Ok(WebTestData.Profile(key: WebTestData.Key(isProvisioned: false)));

        var page = RenderPage<Me>();

        Assert.Contains("being set up", page.Find("[data-testid='key-not-provisioned']").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='key-reveal']"));
    }

    [Fact]
    public void Reveal_shows_the_plaintext_once_and_hiding_takes_it_back()
    {
        var page = RenderPage<Me>();

        page.Find("[data-testid='key-reveal']").Click();

        Assert.Contains("plaintext-key-value", page.Find("[data-testid='key-value']").GetAttribute("value"), StringComparison.Ordinal);
        Assert.NotNull(page.Find("[data-testid='key-reveal-warning']"));
        Assert.Equal(1, Api.CallCount("RevealMyKeyAsync"));

        page.Find("[data-testid='key-hide']").Click();

        Assert.Equal("••••••••1a2b", page.Find("[data-testid='key-value']").GetAttribute("value"));
    }

    [Fact]
    public void Revealing_the_key_fills_it_into_the_cli_snippets()
    {
        var page = RenderPage<Me>();

        // The snippets carry the documented placeholder until the developer asks for the real value.
        Assert.Contains("&lt;your-key&gt;", page.Markup, StringComparison.Ordinal);

        page.Find("[data-testid='key-reveal']").Click();

        Assert.Contains("ANTHROPIC_FOUNDRY_API_KEY=plaintext-key-value", page.Markup, StringComparison.Ordinal);
        Assert.Contains("FOUNDRYGATE_API_KEY=plaintext-key-value", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_reveal_says_so_and_leaves_the_key_masked()
    {
        Api.RevealKeyResult = Fail<FoundryGate.Domain.Keys.Contracts.ApiKeyRevealResponse>("No key to reveal.");

        var page = RenderPage<Me>();
        page.Find("[data-testid='key-reveal']").Click();

        Assert.Equal("••••••••1a2b", page.Find("[data-testid='key-value']").GetAttribute("value"));
        Assert.Contains(Snackbars, s => s.Message?.Contains("No key to reveal.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Rotating_asks_for_confirmation_before_it_touches_the_api()
    {
        var page = RenderPage<Me>();

        page.Find("[data-testid='key-rotate']").Click();
        Assert.Equal(0, Api.CallCount("RotateMyKeyAsync"));

        page.Find("[data-testid='key-rotate-cancel']").Click();
        Assert.Equal(0, Api.CallCount("RotateMyKeyAsync"));

        page.Find("[data-testid='key-rotate']").Click();
        page.Find("[data-testid='key-rotate-confirm']").Click();

        // The dialog's result and the rotate call settle across render cycles, so this waits rather
        // than assuming the click drained them (#195).
        page.WaitForAssertion(() =>
        {
            Assert.Equal(1, Api.CallCount("RotateMyKeyAsync"));
            Assert.Contains("rotated-key-value", page.Find("[data-testid='key-value']").GetAttribute("value"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_rotated_mask_survives_the_next_render()
    {
        var page = RenderPage<Me>();
        page.Find("[data-testid='key-rotate']").Click();
        page.Find("[data-testid='key-rotate-confirm']").Click();
        page.WaitForElement("[data-testid='key-hide']").Click();

        // The component used to write the new value onto its own [Parameter], which the framework
        // owns and overwrites — so any later render reverted the panel to the pre-rotation mask.
        page.WaitForAssertion(() =>
            Assert.Equal(WebTestData.Reveal("rotated-key-value").MaskedKey, page.Find("[data-testid='key-value']").GetAttribute("value")));
    }

    [Fact]
    public void The_cli_panel_shows_the_gateway_paths_and_the_deployment_names()
    {
        Api.MeResult = Ok(WebTestData.Profile(cliConfig: WebTestData.CliConfig("https://ai.example.test/")));
        Api.FoundryModelsResult = Ok<IReadOnlyList<FoundryGate.Domain.Foundry.Contracts.FoundryModelResponse>>(
        [
            WebTestData.Model("claude-sonnet-5"),
            WebTestData.Model("gpt-5-codex", "gpt-5-codex", "OpenAI"),
        ]);

        var page = RenderPage<Me>();

        Assert.Equal("https://ai.example.test/anthropic", page.Find("[data-testid='cli-anthropic-url']").TextContent.Trim());
        Assert.Equal("https://ai.example.test/openai/v1", page.Find("[data-testid='cli-openai-url']").TextContent.Trim());

        var models = page.Find("[data-testid='cli-models']").TextContent;
        Assert.Contains("claude-sonnet-5", models, StringComparison.Ordinal);
        Assert.Contains("gpt-5-codex", models, StringComparison.Ordinal);
    }

    // The snippets themselves are checked against the doc they are copied from, by reading it —
    // see CliSetupDocFidelityTests. A second hard-coded copy here would only drift.

    [Fact]
    public void An_empty_model_list_says_so_rather_than_rendering_nothing()
    {
        Api.FoundryModelsResult = Ok<IReadOnlyList<FoundryGate.Domain.Foundry.Contracts.FoundryModelResponse>>([]);

        var page = RenderPage<Me>();

        Assert.NotNull(page.Find("[data-testid='cli-no-models']"));
    }

    [Fact]
    public void The_history_asks_for_the_callers_own_requests_only()
    {
        Api.MeResult = Ok(WebTestData.Profile(userId: 314));

        _ = RenderPage<Me>();

        // #49 says "own only", and GET /requests hands an admin everyone's — so "Your quota
        // increase requests" has to name the caller rather than take whatever comes back.
        var query = Assert.Single(Api.RequestListCalls).Query;
        Assert.Equal(314, query.UserId);
        Assert.Null(query.Status);
    }

    [Fact]
    public void An_admins_own_page_does_not_list_other_peoples_requests()
    {
        SignInAsAdmin();
        Api.MeResult = Ok(WebTestData.Profile(userId: 1, displayName: "Ada Admin"));

        _ = RenderPage<Me>();

        Assert.Equal(1, Assert.Single(Api.RequestListCalls).Query.UserId);
    }

    [Fact]
    public void An_empty_request_history_invites_a_first_request()
    {
        var page = RenderPage<Me>();

        Assert.NotNull(page.Find("[data-testid='me-requests-empty']"));
        Assert.Empty(page.FindAll("[data-testid='me-requests-table']"));
    }

    [Fact]
    public void Request_history_shows_each_request_with_its_review_state()
    {
        Api.RequestsResult = Ok(WebTestData.Page(
            WebTestData.Request(status: QuotaRequestStatusType.Approved, reviewNotes: "Approved for the migration.")));

        var page = RenderPage<Me>();

        var table = page.Find("[data-testid='me-requests-table']").TextContent;
        Assert.Contains("Approved", table, StringComparison.Ordinal);
        Assert.Contains("20,000,000", table, StringComparison.Ordinal);
        Assert.Contains("Approved for the migration.", table, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_profile_load_renders_an_error_not_a_blank_page()
    {
        Api.MeResult = Fail<FoundryGate.Domain.Users.Contracts.UserProfileResponse>("Foundry Gate's API isn't reachable right now.");

        var page = RenderPage<Me>();

        Assert.Contains("isn't reachable", page.Find("[data-testid='me-load-error']").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-testid='quota-gauge']"));
    }

    [Fact]
    public void An_expired_session_offers_a_way_back_in()
    {
        Api.MeResult = ApiCallResult<FoundryGate.Domain.Users.Contracts.UserProfileResponse>.Fail(
            ApiCallStatus.Unauthorized,
            "Your sign-in has expired. Please sign in again.");

        var page = RenderPage<Me>();

        Assert.Contains("Sign in again", page.Find("[data-testid='me-load-error']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deactivated_account_is_called_out()
    {
        Api.MeResult = Ok(WebTestData.Profile(isActive: false));

        var page = RenderPage<Me>();

        Assert.Contains("deactivated", page.Find("[data-testid='me-inactive']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Shows_a_skeleton_while_the_profile_is_still_loading()
    {
        Api.Gate = new TaskCompletionSource();

        var page = RenderPage<Me>();

        Assert.NotNull(page.Find("[data-testid='me-loading']"));

        Api.Gate.SetResult();
        page.WaitForElement("[data-testid='quota-gauge']");
    }

    private static ApiCallResult<T> Ok<T>(T value) => ApiCallResult<T>.Ok(value);

    private static ApiCallResult<T> Fail<T>(string message) =>
        ApiCallResult<T>.Fail(ApiCallStatus.Unavailable, message);
}
