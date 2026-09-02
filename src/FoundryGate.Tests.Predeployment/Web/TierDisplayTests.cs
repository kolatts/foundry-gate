using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Web.Shared;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// A monthly budget is either unlimited or exactly one configured tier cap
/// (fable-refactor-log.md D-013), so every place the admin UI would have shown a token count
/// shows a tier name instead. <see cref="TierDisplay"/> is the one translation, and it has to
/// keep working for values that predate a tier change — the API resolves those upward rather
/// than failing, and so does this.
/// </summary>
public class TierDisplayTests
{
    [Fact]
    public void A_cap_that_matches_a_tier_is_shown_as_that_tiers_name()
    {
        Assert.Equal("Standard", TierDisplay.Describe(isUnlimited: false, 5_000_000, WebTestData.Tiers()));
        Assert.Equal("Power", TierDisplay.Describe(isUnlimited: false, 20_000_000, WebTestData.Tiers()));
    }

    [Fact]
    public void Unlimited_wins_over_any_cap()
    {
        // The API ignores MonthlyTokenQuota when IsUnlimited is set (spec §3.2 step 1), so the
        // display must not contradict it by showing the stale number.
        Assert.Equal("Unlimited", TierDisplay.Describe(isUnlimited: true, 5_000_000, WebTestData.Tiers()));
    }

    [Fact]
    public void No_cap_and_not_unlimited_means_inherited()
    {
        Assert.Equal(TierDisplay.InheritedLabel, TierDisplay.Describe(isUnlimited: false, null, WebTestData.Tiers()));
    }

    [Fact]
    public void A_legacy_value_that_matches_no_tier_is_shown_as_itself_rather_than_hidden()
    {
        // IsGatewayCapped exists precisely because such rows survive a tier change; showing
        // nothing, or the wrong tier, would be worse than showing the number.
        Assert.Equal("1M tokens", TierDisplay.Describe(isUnlimited: false, 1_000_000, WebTestData.Tiers()));
    }

    [Fact]
    public void Describe_survives_the_tier_catalogue_not_having_loaded_yet()
    {
        Assert.Equal("Unlimited", TierDisplay.Describe(isUnlimited: true, null, tiers: null));
        Assert.Equal("5M tokens", TierDisplay.Describe(isUnlimited: false, 5_000_000, tiers: null));
    }

    [Fact]
    public void MatchProductId_selects_the_tier_a_stored_quota_came_from()
    {
        Assert.Equal(GatewayTiers.Standard, TierDisplay.MatchProductId(isUnlimited: false, 5_000_000, WebTestData.Tiers()));
        Assert.Equal(GatewayTiers.Unlimited, TierDisplay.MatchProductId(isUnlimited: true, null, WebTestData.Tiers()));
        Assert.Null(TierDisplay.MatchProductId(isUnlimited: false, null, WebTestData.Tiers()));
        Assert.Null(TierDisplay.MatchProductId(isUnlimited: false, 1_000_000, WebTestData.Tiers()));
    }

    [Theory]
    [InlineData(500L, "500 tokens")]
    [InlineData(5_000L, "5K tokens")]
    [InlineData(5_000_000L, "5M tokens")]
    [InlineData(2_000_000_000L, "2B tokens")]
    [InlineData(1_500L, "1,500 tokens")]
    public void Token_counts_read_the_way_an_admin_says_them(long tokens, string expected) =>
        Assert.Equal(expected, TierDisplay.FormatTokens(tokens));

    [Fact]
    public void Every_resolved_level_has_wording_that_says_where_the_budget_came_from()
    {
        foreach (var level in Enum.GetValues<QuotaLevelType>())
        {
            var label = TierDisplay.LevelLabel(level);
            Assert.False(string.IsNullOrWhiteSpace(label));

            // The enum name leaking through means a new level was added without wording.
            Assert.NotEqual(level.ToString(), label);
        }
    }
}
