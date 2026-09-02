using Bunit;
using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace FoundryGate.Tests.Predeployment.Web.Pages;

/// <summary>
/// <c>/foundry</c> (#62): the grid shows what ARM reports, deletes are confirmed, and an
/// Anthropic-format deployment can't be deleted from here at all — the API refuses it, because
/// Claude deployments belong to infra end to end (plans/20-foundry-provisioning.md).
/// </summary>
public class FoundryPageTests : WebTestContext
{
    public FoundryPageTests() => SignInAsAdmin();

    [Fact]
    public void Renders_the_account_model_capacity_and_state_of_every_deployment()
    {
        Api.ArrangeDeployments(
            WebTestData.Deployment(accountName: "fg-eastus", deploymentName: "gpt-5-codex", capacity: 25),
            WebTestData.Deployment(accountName: "fg-swedencentral", deploymentName: "claude-opus-5", format: FoundryModelFormatType.Anthropic, provisioningState: "Creating"));

        var page = RenderPage<Foundry>();

        page.WaitForAssertion(() =>
        {
            var markup = page.Markup;
            Assert.Contains("fg-eastus", markup, StringComparison.Ordinal);
            Assert.Contains("gpt-5-codex", markup, StringComparison.Ordinal);

            // Capacity is thousands of TPM, and the column says so rather than showing a bare 25.
            Assert.Contains("25K TPM", markup, StringComparison.Ordinal);
            Assert.Contains("Succeeded", page.Find("[data-testid=foundry-state-gpt-5-codex]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Creating", page.Find("[data-testid=foundry-state-claude-opus-5]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void An_anthropic_deployment_cannot_be_deleted_from_here()
    {
        Api.ArrangeDeployments(WebTestData.Deployment(deploymentName: "claude-opus-5", format: FoundryModelFormatType.Anthropic));

        var page = RenderPage<Foundry>();

        page.WaitForAssertion(() =>
        {
            var button = page.Find("[data-testid=foundry-delete-claude-opus-5]");
            Assert.True(button.HasAttribute("disabled"));
        });

        page.Find("[data-testid=foundry-delete-claude-opus-5]").Click();
        Assert.Empty(Api.DeletedDeployments);
    }

    [Fact]
    public void Cancelling_the_delete_confirmation_leaves_the_deployment_alone()
    {
        Api.ArrangeDeployments(WebTestData.Deployment(deploymentName: "gpt-5-codex"));

        var page = RenderPage<Foundry>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=foundry-delete-gpt-5-codex]")));

        page.Find("[data-testid=foundry-delete-gpt-5-codex]").Click();
        page.WaitForElement("[data-testid=confirm-cancel]").Click();

        page.WaitForAssertion(() => Assert.Empty(Api.DeletedDeployments));
    }

    [Fact]
    public void Confirming_deletes_the_named_deployment_in_its_own_account()
    {
        Api.ArrangeDeployments(WebTestData.Deployment(accountName: "fg-eastus", deploymentName: "gpt-5-codex"));

        var page = RenderPage<Foundry>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=foundry-delete-gpt-5-codex]")));

        page.Find("[data-testid=foundry-delete-gpt-5-codex]").Click();
        page.WaitForElement("[data-testid=confirm-ok]").Click();

        page.WaitForAssertion(() => Assert.Equal(("fg-eastus", "gpt-5-codex"), Assert.Single(Api.DeletedDeployments)));
    }

    [Fact]
    public void The_create_dialog_sends_an_openai_deployment_and_says_why_claude_isnt_offered()
    {
        Api.ArrangeDeployments(WebTestData.Deployment(accountName: "fg-eastus"));
        Api.CreateFoundryDeploymentResult = ApiCallResult<FoundryDeploymentResponse>.Ok(WebTestData.Deployment(deploymentName: "gpt-5-1-codex-max"));

        var page = RenderPage<Foundry>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=foundry-new]")));

        page.Find("[data-testid=foundry-new]").Click();
        page.WaitForElement("[data-testid=deployment-create]");

        Assert.Contains("Claude deployments aren't created here", page.Markup, StringComparison.Ordinal);

        page.Find("input[data-testid=deployment-name]").Change("gpt-5-1-codex-max");
        page.Find("input[data-testid=deployment-model]").Input("gpt-5.1-codex-max");
        page.Find("input[data-testid=deployment-version]").Input("2026-01-01");
        page.Find("input[data-testid=deployment-sku]").Input("GlobalStandard");
        page.Find("[data-testid=deployment-create]").Click();

        page.WaitForAssertion(() =>
        {
            var created = Assert.Single(Api.CreatedDeployments);
            Assert.Equal(FoundryModelFormatType.OpenAI, created.ModelFormat);
            Assert.Equal("gpt-5-1-codex-max", created.DeploymentName);

            // The single known account is pre-selected, so the common case is not a guess.
            Assert.Equal("fg-eastus", created.AccountName);
        });
    }

    [Fact]
    public void The_create_dialog_offers_what_the_accounts_can_actually_serve()
    {
        // #173: the model and SKU suggestions came from a hardcoded array in the dialog until
        // GET /foundry/catalog existed. A hardcoded model list goes stale the week after it ships.
        Api.ArrangeDeployments(WebTestData.Deployment(accountName: "fg-eastus"));
        Api.ArrangeCatalog(WebTestData.CatalogEntry("gpt-5-codex", "2026-01-01", ["DataZoneStandard", "GlobalStandard"], defaultCapacity: 30));

        var page = RenderPage<Foundry>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=foundry-new]")));
        page.Find("[data-testid=foundry-new]").Click();
        page.WaitForElement("[data-testid=deployment-create]");

        page.WaitForAssertion(() =>
            Assert.Contains("gpt-5-codex", SearchOptions(page, "deployment-model", string.Empty), StringComparer.Ordinal));
        Assert.Empty(page.FindAll("[data-testid=deployment-catalog-unavailable]"));
    }

    [Fact]
    public void Choosing_a_catalogued_model_fills_in_its_version_sku_and_suggested_capacity()
    {
        Api.ArrangeDeployments(WebTestData.Deployment(accountName: "fg-eastus"));
        Api.ArrangeCatalog(WebTestData.CatalogEntry("gpt-5-codex", "2026-01-01", ["DataZoneStandard", "GlobalStandard"], defaultCapacity: 30));
        Api.CreateFoundryDeploymentResult = ApiCallResult<FoundryDeploymentResponse>.Ok(WebTestData.Deployment(deploymentName: "codex"));

        var page = RenderPage<Foundry>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=foundry-new]")));
        page.Find("[data-testid=foundry-new]").Click();
        page.WaitForElement("[data-testid=deployment-create]");

        page.Find("input[data-testid=deployment-name]").Change("codex");
        page.Find("input[data-testid=deployment-model]").Input("gpt-5-codex");
        page.Find("[data-testid=deployment-create]").Click();

        // One pick, not four: the catalogue knows the version, the SKUs and the capacity ARM suggests.
        page.WaitForAssertion(() =>
        {
            var created = Assert.Single(Api.CreatedDeployments);
            Assert.Equal("gpt-5-codex", created.ModelName);
            Assert.Equal("2026-01-01", created.ModelVersion);
            Assert.Equal("DataZoneStandard", created.SkuName);
            Assert.Equal(30, created.Capacity);
        });
    }

    [Fact]
    public void A_model_the_catalogue_never_listed_is_still_deployable()
    {
        // Coercion stays: Azure lists models before this endpoint does, and Azure decides the create.
        Api.ArrangeDeployments(WebTestData.Deployment(accountName: "fg-eastus"));
        Api.ArrangeCatalog(WebTestData.CatalogEntry("gpt-5-codex", "2026-01-01", ["GlobalStandard"]));
        Api.CreateFoundryDeploymentResult = ApiCallResult<FoundryDeploymentResponse>.Ok(WebTestData.Deployment(deploymentName: "brand-new"));

        var page = RenderPage<Foundry>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=foundry-new]")));
        page.Find("[data-testid=foundry-new]").Click();
        page.WaitForElement("[data-testid=deployment-create]");

        page.Find("input[data-testid=deployment-name]").Change("brand-new");
        page.Find("input[data-testid=deployment-model]").Input("gpt-6-not-yet-listed");
        page.Find("input[data-testid=deployment-version]").Input("2026-09-01");
        page.Find("input[data-testid=deployment-sku]").Input("GlobalStandard");
        page.Find("[data-testid=deployment-create]").Click();

        page.WaitForAssertion(() => Assert.Equal("gpt-6-not-yet-listed", Assert.Single(Api.CreatedDeployments).ModelName));
    }

    [Fact]
    public void An_unreadable_catalogue_says_so_and_leaves_every_field_free_text()
    {
        // The dialog's job is creating a deployment; losing the shortcut must not lose the form.
        Api.ArrangeDeployments(WebTestData.Deployment(accountName: "fg-eastus"));
        Api.FoundryCatalogResult = ApiCallResult<IReadOnlyList<FoundryCatalogEntryResponse>>.Fail(
            ApiCallStatus.Error, "Couldn't reach the Foundry accounts.");
        Api.CreateFoundryDeploymentResult = ApiCallResult<FoundryDeploymentResponse>.Ok(WebTestData.Deployment(deploymentName: "typed"));

        var page = RenderPage<Foundry>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=foundry-new]")));
        page.Find("[data-testid=foundry-new]").Click();
        page.WaitForElement("[data-testid=deployment-create]");

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=deployment-catalog-unavailable]")));

        page.Find("input[data-testid=deployment-name]").Change("typed");
        page.Find("input[data-testid=deployment-model]").Input("gpt-4.1-mini");
        page.Find("input[data-testid=deployment-version]").Input("2025-04-14");
        page.Find("input[data-testid=deployment-sku]").Input("GlobalStandard");
        page.Find("[data-testid=deployment-create]").Click();

        page.WaitForAssertion(() => Assert.Equal("gpt-4.1-mini", Assert.Single(Api.CreatedDeployments).ModelName));
    }

    /// <summary>
    /// Runs one autocomplete's own <c>SearchFunc</c> — MudBlazor's list lives in a popover the
    /// headless renderer never opens, so the search is driven directly.
    /// </summary>
    private static IReadOnlyList<string> SearchOptions(IRenderedComponent<IComponent> page, string testId, string term)
    {
        var autocomplete = page.FindComponents<MudAutocomplete<string>>()
            .Single(c => string.Equals(c.Instance.UserAttributes["data-testid"] as string, testId, StringComparison.Ordinal));

        // Declared non-nullable: the compiler resets a nullable local's flow state inside a lambda.
        Func<string, CancellationToken, Task<IEnumerable<string>?>> search = autocomplete.Instance.SearchFunc!;

        return page.InvokeAsync(async () => (await search(term, CancellationToken.None))?.ToList() ?? [])
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void Cancelling_the_create_dialog_sends_nothing()
    {
        Api.ArrangeDeployments(WebTestData.Deployment());

        var page = RenderPage<Foundry>();
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=foundry-new]")));

        page.Find("[data-testid=foundry-new]").Click();
        page.WaitForElement("[data-testid=deployment-cancel]").Click();

        page.WaitForAssertion(() => Assert.Empty(Api.CreatedDeployments));
    }

    [Fact]
    public void A_503_names_the_configuration_problem_instead_of_saying_something_went_wrong()
    {
        // FeatureNotConfiguredException's message names the configuration keys an operator has to
        // fix, so it belongs on screen whole.
        Api.FoundryDeploymentsResult = ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>
            .Fail(ApiCallStatus.Error, "Gateway:FoundryAccountNames names an account Azure does not have: fg-nowhere.");

        var page = RenderPage<Foundry>();

        page.WaitForAssertion(() =>
            Assert.Contains("fg-nowhere", page.Find("[data-testid=foundry-error]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void A_403_from_the_api_renders_access_denied()
    {
        Api.FoundryDeploymentsResult = ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>
            .Fail(ApiCallStatus.Forbidden, "You don't have permission to do that.");

        var page = RenderPage<Foundry>();

        page.WaitForAssertion(() => Assert.Contains("Access denied", page.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_account_says_so()
    {
        var page = RenderPage<Foundry>();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=foundry-empty]")));
    }
}
