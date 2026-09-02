using FoundryGate.Domain.Constants;
using FoundryGate.Web.Services;
using MudBlazor;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The quota gauge's colour thresholds (#49) as a table, so the boundaries are pinned by name
/// rather than inferred from a rendered CSS class: green below 80%, amber from 80% through 95%,
/// red above 95%.
/// </summary>
public class QuotaDisplayTests
{
    [Theory]
    [InlineData(0, "Success")]
    [InlineData(79.9, "Success")]
    [InlineData(80, "Warning")]
    [InlineData(94.9, "Warning")]
    [InlineData(95, "Warning")]
    [InlineData(95.1, "Error")]
    [InlineData(100, "Error")]
    [InlineData(140, "Error")]
    public void GaugeColor_follows_the_documented_thresholds(double percentUsed, string expected) =>
        Assert.Equal(expected, QuotaDisplay.GaugeColor(percentUsed).ToString());

    [Fact]
    public void GaugeColor_of_an_unlimited_allocation_is_not_an_alarm()
    {
        // Unlimited has no bar to colour — callers render the "Unlimited" chip instead — but this
        // must never answer Error for a null, or an unlimited developer's row would look critical.
        Assert.Equal(Color.Success, QuotaDisplay.GaugeColor(null));
    }

    [Theory]
    [InlineData(null, 0d)]
    [InlineData(-5d, 0d)]
    [InlineData(42d, 42d)]
    [InlineData(180d, 100d)]
    public void GaugeValue_clamps_into_the_bar_range(double? percentUsed, double expected) =>
        Assert.Equal(expected, QuotaDisplay.GaugeValue(percentUsed));

    [Theory]
    [InlineData(GatewayTiers.Standard, "Standard")]
    [InlineData(GatewayTiers.Power, "Power")]
    [InlineData(GatewayTiers.Unlimited, "Unlimited")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("", "Unknown tier")]
    public void TierDisplayName_names_configured_tiers_and_title_cases_a_fork_added_one(string productId, string expected) =>
        Assert.Equal(expected, QuotaDisplay.TierDisplayName(productId));

    [Fact]
    public void FormatTokens_calls_a_null_allocation_unlimited() =>
        Assert.Equal("Unlimited", QuotaDisplay.FormatTokens(null));
}
