using Bunit;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// <c>/me/request</c> (#50). The behaviour that matters is the D-013 one: the form asks for a
/// <em>tier</em>, offers only tiers above the caller's current budget, and never lets a second
/// request go out while one is pending.
/// </summary>
public class MeRequestPageTests : WebTestContext
{
    public MeRequestPageTests() => SignInAsDeveloper();

    [Fact]
    public void Offers_only_the_tiers_above_the_current_one()
    {
        var offered = MeRequest.TiersAbove(WebTestData.Tiers(), currentQuota: 5_000_000, currentIsUnlimited: false);

        Assert.Equal([GatewayTiers.Power, GatewayTiers.Unlimited], offered.Select(t => t.ProductId));
    }

    [Fact]
    public void Offers_nothing_to_a_developer_who_is_already_unlimited()
    {
        var offered = MeRequest.TiersAbove(WebTestData.Tiers(), currentQuota: null, currentIsUnlimited: true);

        Assert.Empty(offered);
    }

    [Fact]
    public void Orders_finite_tiers_by_size_and_puts_unlimited_last()
    {
        var offered = MeRequest.TiersAbove(WebTestData.Tiers(), currentQuota: 0, currentIsUnlimited: false);

        Assert.Equal([GatewayTiers.Standard, GatewayTiers.Power, GatewayTiers.Unlimited], offered.Select(t => t.ProductId));
    }

    [Fact]
    public void Renders_the_form_with_the_current_tier_named()
    {
        var page = RenderPage<MeRequest>();

        Assert.NotNull(page.Find("[data-testid='request-tier']"));
        Assert.NotNull(page.Find("[data-testid='request-justification']"));
        Assert.Contains("Standard", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Locks_the_form_when_a_request_is_already_pending()
    {
        Api.RequestsResult = ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>.Ok(
            WebTestData.Page(WebTestData.Request(status: QuotaRequestStatusType.Pending)));

        var page = RenderPage<MeRequest>();

        Assert.Contains("waiting for review", page.Find("[data-testid='request-pending']").TextContent, StringComparison.Ordinal);
        Assert.True(page.Find("[data-testid='request-submit']").HasAttribute("disabled"));
    }

    [Fact]
    public void Asks_the_api_only_for_pending_requests_when_checking()
    {
        _ = RenderPage<MeRequest>();

        var query = Assert.Single(Api.FilteredRequestQueries).Query;
        Assert.Equal(QuotaRequestStatusType.Pending, query.Status);
    }

    [Fact]
    public void Says_there_is_nothing_bigger_to_ask_for_when_already_on_the_top_tier()
    {
        Api.MeResult = ApiCallResult<FoundryGate.Domain.Users.Contracts.UserProfileResponse>.Ok(
            WebTestData.Profile(quota: WebTestData.Allocation(isUnlimited: true, tierProductId: GatewayTiers.Unlimited)));

        var page = RenderPage<MeRequest>();

        Assert.NotNull(page.Find("[data-testid='request-no-tiers']"));
        Assert.True(page.Find("[data-testid='request-submit']").HasAttribute("disabled"));
    }

    [Fact]
    public void Refuses_a_justification_shorter_than_the_domain_record_allows()
    {
        var page = RenderPage<MeRequest>();
        SelectTier(page, GatewayTiers.Power);
        Type(page, "too short");

        page.Find("[data-testid='request-submit']").Click();

        Assert.Empty(Api.SubmittedRequests);
        Assert.Contains(Snackbars, s => s.Severity == MudBlazor.Severity.Warning);
    }

    [Fact]
    public void Submits_the_selected_tiers_cap_as_the_requested_quota()
    {
        var page = RenderPage<MeRequest>();
        SelectTier(page, GatewayTiers.Power);
        Type(page, "We are migrating the monolith and burning through tokens.");

        page.Find("[data-testid='request-submit']").Click();

        var submitted = Assert.Single(Api.SubmittedRequests);
        Assert.Equal(20_000_000, submitted.RequestedQuota);
        Assert.Contains("migrating", submitted.Justification, StringComparison.Ordinal);
    }

    [Fact]
    public void Requesting_the_unlimited_tier_sends_a_null_quota()
    {
        var page = RenderPage<MeRequest>();
        SelectTier(page, GatewayTiers.Unlimited);
        Type(page, "I run the nightly evaluation fleet and cannot predict the volume.");

        page.Find("[data-testid='request-submit']").Click();

        Assert.Null(Assert.Single(Api.SubmittedRequests).RequestedQuota);
    }

    [Fact]
    public void Navigates_back_to_me_after_a_successful_submission()
    {
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var page = RenderPage<MeRequest>();
        SelectTier(page, GatewayTiers.Power);
        Type(page, "We are migrating the monolith and burning through tokens.");

        page.Find("[data-testid='request-submit']").Click();

        Assert.EndsWith("/me", navigation.Uri, StringComparison.Ordinal);
        Assert.Contains(Snackbars, s => s.Severity == MudBlazor.Severity.Success);
    }

    [Fact]
    public void A_409_warns_and_locks_the_form_without_navigating_away()
    {
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var startingUri = navigation.Uri;
        Api.SubmitRequestResult = ApiCallResult<QuotaIncreaseRequestResponse>.Fail(
            ApiCallStatus.Error,
            "You already have a request waiting for review.",
            new ApiError("about:blank", "Conflict", 409, "You already have a request waiting for review."));

        var page = RenderPage<MeRequest>();
        SelectTier(page, GatewayTiers.Power);
        Type(page, "We are migrating the monolith and burning through tokens.");

        page.Find("[data-testid='request-submit']").Click();

        Assert.Equal(startingUri, navigation.Uri);
        Assert.NotNull(page.Find("[data-testid='request-pending']"));
        Assert.Contains(Snackbars, s => s.Severity == MudBlazor.Severity.Warning);
    }

    [Fact]
    public void Submit_is_disabled_while_the_request_is_in_flight()
    {
        var page = RenderPage<MeRequest>();
        SelectTier(page, GatewayTiers.Power);
        Type(page, "We are migrating the monolith and burning through tokens.");

        Api.Gate = new TaskCompletionSource();
        page.Find("[data-testid='request-submit']").Click();

        var submit = page.Find("[data-testid='request-submit']");
        Assert.True(submit.HasAttribute("disabled"));
        Assert.Contains("Submitting", submit.TextContent, StringComparison.Ordinal);

        Api.Gate.SetResult();
    }

    [Fact]
    public void A_failed_tier_load_renders_an_error_instead_of_an_empty_dropdown()
    {
        Api.QuotaTiersResult = ApiCallResult<IReadOnlyList<QuotaTierResponse>>.Fail(
            ApiCallStatus.Unavailable,
            "Foundry Gate's API isn't reachable right now.");

        var page = RenderPage<MeRequest>();

        Assert.Contains("isn't reachable", page.Find("[data-testid='request-load-error']").TextContent, StringComparison.Ordinal);
    }

    private static void SelectTier(IRenderedComponent<Bunit.Rendering.ContainerFragment> page, string productId)
    {
        // MudSelect's own dropdown needs a popover the headless renderer never opens, so the
        // selection is made the way the component would make it: through the bound value change,
        // on the renderer's dispatcher.
        var select = page.FindComponent<MudBlazor.MudSelect<string>>();
        page.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(productId)).GetAwaiter().GetResult();
    }

    private static void Type(IRenderedComponent<Bunit.Rendering.ContainerFragment> page, string justification) =>
        page.Find("[data-testid='request-justification']").Input(justification);
}
