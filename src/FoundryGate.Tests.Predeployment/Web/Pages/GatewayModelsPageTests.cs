using Bunit;
using FoundryGate.Domain.Config;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Gateway.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web.Pages;

/// <summary>
/// <c>/models</c> (#225): what each tier is allowed to use, and the three edits that change it.
/// Every edit is a full replace of the tier's alias map — the gateway reads one JSON document — so
/// the assertions are about the map that was <em>sent</em>, not about which row was clicked.
/// </summary>
public class GatewayModelsPageTests : WebTestContext
{
    public GatewayModelsPageTests() => SignInAsAdmin();

    [Fact]
    public void Shows_each_tiers_allowed_models_and_what_developers_would_see()
    {
        Api.ArrangeDeployments(WebTestData.Deployment(deploymentName: "claude-sonnet-4-5", format: FoundryModelFormatType.Anthropic));
        Api.ArrangeTierModels(GatewayTiers.Standard, WebTestData.ModelAlias(alias: "sonnet"));
        Api.ArrangeTierModels(GatewayTiers.Unlimited, WebTestData.ModelAlias(alias: "opus", deploymentName: "claude-opus-4-5"));

        var page = RenderPage<GatewayModels>();

        page.WaitForAssertion(() =>
        {
            // The overview says which tiers permit which alias — including, by "—", which do not.
            Assert.Contains("claude-sonnet-4-5", page.Find($"[data-testid=models-cell-{GatewayTiers.Standard}-sonnet]").TextContent, StringComparison.Ordinal);
            Assert.Contains("—", page.Find($"[data-testid=models-cell-{GatewayTiers.Power}-sonnet]").TextContent, StringComparison.Ordinal);

            // The preview is the developer's-eye view: the aliases, not the deployments behind them.
            var preview = page.Find($"[data-testid=models-preview-{GatewayTiers.Standard}]").TextContent;
            Assert.Contains("sonnet", preview, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_tier_that_allows_nothing_says_so_rather_than_looking_empty()
    {
        // A tier with no map permits nothing, which is a working configuration and a broken one
        // depending on intent — so the page names the consequence instead of rendering a blank table.
        var page = RenderPage<GatewayModels>();

        page.WaitForAssertion(() =>
            Assert.Contains(
                "allows no models at all",
                page.Find($"[data-testid=models-empty-{GatewayTiers.Standard}]").TextContent,
                StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_whose_deployment_is_gone_is_flagged()
    {
        Api.ArrangeTierModels(
            GatewayTiers.Standard,
            WebTestData.ModelAlias(alias: "ghost", deploymentName: "deleted-model", deploymentExists: false));

        var page = RenderPage<GatewayModels>();

        page.WaitForAssertion(() =>
            Assert.Contains(
                "no such deployment",
                page.Find($"[data-testid=models-missing-{GatewayTiers.Standard}-ghost]").TextContent,
                StringComparison.Ordinal));
    }

    [Fact]
    public void A_pooled_row_missing_from_a_region_is_flagged_separately()
    {
        // Different failure from "no such deployment": the model exists, it just cannot survive the
        // pool's own failover, which is the shape that turns a 429 into a 404.
        Api.ArrangeTierModels(
            GatewayTiers.Standard,
            WebTestData.ModelAlias(alias: "sonnet", missingFromAccounts: ["fg-swedencentral"]));

        var page = RenderPage<GatewayModels>();

        page.WaitForAssertion(() =>
            Assert.Contains(
                "not in every region",
                page.Find($"[data-testid=models-partial-{GatewayTiers.Standard}-sonnet]").TextContent,
                StringComparison.Ordinal));
    }

    [Fact]
    public void Allowing_a_model_sends_the_whole_map_with_the_new_row_added()
    {
        Api.ArrangeDeployments(WebTestData.Deployment(deploymentName: "gpt-5-codex"));
        Api.ArrangeTierModels(GatewayTiers.Standard, WebTestData.ModelAlias(alias: "sonnet"));

        var page = RenderPage<GatewayModels>();
        page.WaitForElement($"[data-testid=models-add-{GatewayTiers.Standard}]");

        page.Find($"[data-testid=models-add-{GatewayTiers.Standard}]").Click();
        page.WaitForElement("[data-testid=alias-save]");

        page.Find("input[data-testid=alias-name]").Change("codex");
        page.Find("[data-testid=alias-save]").Click();

        page.WaitForAssertion(() =>
        {
            var (tier, request) = Assert.Single(Api.ReplacedTierModels);
            Assert.Equal(GatewayTiers.Standard, tier);

            // The existing row travels with the new one: this is a replace, and dropping what was
            // already allowed would silently revoke it.
            Assert.Contains(request.Aliases, alias => alias.Alias == "sonnet");
            var added = Assert.Single(request.Aliases, alias => alias.Alias == "codex");
            Assert.Equal("gpt-5-codex", added.DeploymentName);

            // Provider and pool are derived from the deployment's model format, never asked.
            Assert.Equal(ModelProviderType.OpenAi, added.Provider);
            Assert.Equal(GatewayModelMap.OpenAiPool, added.Pool);
        });
    }

    [Fact]
    public void An_anthropic_deployment_derives_the_anthropic_front_door_and_the_pool()
    {
        Api.ArrangeDeployments(WebTestData.Deployment(deploymentName: "claude-haiku-4-5", format: FoundryModelFormatType.Anthropic));

        var page = RenderPage<GatewayModels>();
        page.WaitForElement($"[data-testid=models-add-{GatewayTiers.Power}]");

        page.Find($"[data-testid=models-add-{GatewayTiers.Power}]").Click();
        page.WaitForElement("[data-testid=alias-save]");

        page.Find("input[data-testid=alias-name]").Change("haiku");
        page.Find("[data-testid=alias-save]").Click();

        page.WaitForAssertion(() =>
        {
            var (_, request) = Assert.Single(Api.ReplacedTierModels);
            var added = Assert.Single(request.Aliases);
            Assert.Equal(ModelProviderType.Anthropic, added.Provider);
            Assert.Equal(GatewayModelMap.AnthropicPool, added.Pool);
        });
    }

    [Fact]
    public void Removing_a_model_asks_first_and_then_sends_the_map_without_it()
    {
        Api.ArrangeTierModels(
            GatewayTiers.Standard,
            WebTestData.ModelAlias(alias: "sonnet"),
            WebTestData.ModelAlias(alias: "haiku", deploymentName: "claude-haiku-4-5"));

        var page = RenderPage<GatewayModels>();
        page.WaitForElement($"[data-testid=models-remove-{GatewayTiers.Standard}-haiku]");

        page.Find($"[data-testid=models-remove-{GatewayTiers.Standard}-haiku]").Click();
        page.WaitForElement("[data-testid=confirm-ok]");
        page.Find("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() =>
        {
            var (_, request) = Assert.Single(Api.ReplacedTierModels);
            var kept = Assert.Single(request.Aliases);
            Assert.Equal("sonnet", kept.Alias);
        });
    }

    [Fact]
    public void A_refused_replace_says_what_the_api_said()
    {
        // "That deployment isn't in every region" is a sentence an admin can act on; reducing it to
        // "couldn't save" would throw away the only useful part of the refusal.
        Api.ArrangeTierModels(GatewayTiers.Standard, WebTestData.ModelAlias(alias: "sonnet"));
        Api.ReplaceGatewayTierModelsResult = ApiCallResult<GatewayTierModelsResponse>.Fail(
            ApiCallStatus.Error,
            "Alias 'sonnet' routes at the 'anthropic' pool, so it must exist in every Foundry account.");

        var page = RenderPage<GatewayModels>();
        page.WaitForElement($"[data-testid=models-remove-{GatewayTiers.Standard}-sonnet]");

        page.Find($"[data-testid=models-remove-{GatewayTiers.Standard}-sonnet]").Click();
        page.WaitForElement("[data-testid=confirm-ok]");
        page.Find("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() =>
            Assert.Contains(Snackbars, snackbar => snackbar.Message?.Contains("every Foundry account", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void A_deployment_arriving_from_the_foundry_page_is_offered_for_a_tier()
    {
        // The follow-through from /foundry: a model nobody is allowed to use is not yet useful.
        Api.ArrangeDeployments(WebTestData.Deployment(deploymentName: "gpt-5-codex"));

        var page = RenderPage<GatewayModels>(("DeploymentQuery", "gpt-5-codex"));

        page.WaitForAssertion(() =>
            Assert.Contains(
                "isn't allowed for any tier yet",
                page.Find("[data-testid=models-pending-deployment]").TextContent,
                StringComparison.Ordinal));
    }

    [Fact]
    public void A_deployment_that_is_already_allowed_is_not_offered_again()
    {
        Api.ArrangeDeployments(WebTestData.Deployment(deploymentName: "claude-sonnet-4-5", format: FoundryModelFormatType.Anthropic));
        Api.ArrangeTierModels(GatewayTiers.Standard, WebTestData.ModelAlias(alias: "sonnet"));

        var page = RenderPage<GatewayModels>(("DeploymentQuery", "claude-sonnet-4-5"));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll($"[data-testid=models-panel-{GatewayTiers.Standard}]")));
        Assert.Empty(page.FindAll("[data-testid=models-pending-deployment]"));
    }

    [Fact]
    public void A_403_from_the_api_renders_access_denied()
    {
        Api.GatewayTiersResult = ApiCallResult<IReadOnlyList<GatewayTierResponse>>
            .Fail(ApiCallStatus.Forbidden, "You don't have permission to do that.");

        var page = RenderPage<GatewayModels>();

        page.WaitForAssertion(() => Assert.Contains("Access denied", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void A_503_names_the_configuration_the_operator_has_to_fix()
    {
        Api.GatewayTiersResult = ApiCallResult<IReadOnlyList<GatewayTierResponse>>
            .Fail(ApiCallStatus.Error, "The gateway's model allowlist is not configured: set Gateway:ApimName.");

        var page = RenderPage<GatewayModels>();

        page.WaitForAssertion(() =>
            Assert.Contains("Gateway:ApimName", page.Find("[data-testid=models-error]").TextContent, StringComparison.Ordinal));
    }
}
