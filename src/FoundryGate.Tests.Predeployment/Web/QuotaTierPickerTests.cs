using Bunit;
using FoundryGate.Domain.Constants;
using FoundryGate.Web.Shared;
using MudBlazor;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The only control in the admin UI that sets a monthly budget. A budget IS a gateway tier
/// (fable-refactor-log.md D-013), so this must offer a pick from <c>GET /quota/tiers</c> and
/// never a free-form number — a typed value the gateway can't enforce is exactly what D-013
/// removed.
/// </summary>
public class QuotaTierPickerTests : WebTestContext
{
    [Fact]
    public void Offers_no_numeric_input_at_all()
    {
        var picker = Render<QuotaTierPicker>(p => p.Add(x => x.Tiers, WebTestData.Tiers()));

        Assert.Empty(picker.FindAll("input[type=number]"));
    }

    [Fact]
    public void Lists_every_capped_tier_by_display_name_with_its_cap()
    {
        var picker = Render<QuotaTierPicker>(p => p.Add(x => x.Tiers, WebTestData.Tiers()));

        // MudSelect renders its items into a popover, so they are components in the render tree
        // rather than markup here. Unlimited is the switch, not an item: the select offers
        // "inherited" plus the two capped tiers.
        var items = picker.FindComponents<MudSelectItem<string>>();
        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.Instance.Value == GatewayTiers.Standard);
        Assert.Contains(items, i => i.Instance.Value == GatewayTiers.Power);
        Assert.DoesNotContain(items, i => i.Instance.Value == GatewayTiers.Unlimited);
    }

    [Fact]
    public void Turning_on_unlimited_clears_the_cap_so_the_two_cannot_disagree()
    {
        var isUnlimited = false;
        long? quota = 5_000_000;

        var picker = Render<QuotaTierPicker>(p => p
            .Add(x => x.Tiers, WebTestData.Tiers())
            .Add(x => x.IsUnlimited, false)
            .Add(x => x.MonthlyTokenQuota, 5_000_000L)
            .Add(x => x.IsUnlimitedChanged, value => isUnlimited = value)
            .Add(x => x.MonthlyTokenQuotaChanged, value => quota = value));

        picker.Find("input[type=checkbox]").Change(true);

        Assert.True(isUnlimited);
        Assert.Null(quota);
    }

    [Fact]
    public void The_tier_select_is_disabled_while_unlimited_is_on()
    {
        var picker = Render<QuotaTierPicker>(p => p
            .Add(x => x.Tiers, WebTestData.Tiers())
            .Add(x => x.IsUnlimited, true));

        Assert.True(picker.Find(".mud-select input").HasAttribute("disabled"));
    }

    [Fact]
    public void An_empty_tier_catalogue_still_renders_rather_than_throwing()
    {
        var picker = Render<QuotaTierPicker>(p => p.Add(x => x.Tiers, []));

        Assert.NotEmpty(picker.Markup);
    }
}
