using FoundryGate.Core.Configuration;
using FoundryGate.Core.Quota;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Core.Quota;

/// <summary>
/// "A budget is a tier" (D-013), pinned from both faces: <see cref="GatewayTierMapper.Map"/> is an exact
/// match on a cap (anything else is enforced at the next tier up and flagged, never thrown, so legacy
/// rows keep reading), and <see cref="GatewayTierMapper.EnsureValidQuota"/> is the write-path guard
/// that refuses a non-tier number with the allowed values in the message.
/// </summary>
public class GatewayTierMapperTests
{
    private readonly GatewayTierMapper _mapper = TestGatewayTiers.Mapper();

    [Fact]
    public void Null_quota_maps_to_the_unlimited_tier()
    {
        Assert.Equal(new GatewayTierAssignment(GatewayTiers.Unlimited, false), _mapper.Map(null));
    }

    [Theory]
    [InlineData(TestGatewayTiers.StandardCap, GatewayTiers.Standard)]
    [InlineData(TestGatewayTiers.PowerCap, GatewayTiers.Power)]
    public void Quota_equal_to_a_tier_cap_maps_to_that_tier_uncapped(long quota, string expectedTier)
    {
        Assert.Equal(new GatewayTierAssignment(expectedTier, false), _mapper.Map(quota));
    }

    [Theory]
    [InlineData(0L, GatewayTiers.Standard)] // legacy zero → smallest tier
    [InlineData(1L, GatewayTiers.Standard)]
    [InlineData(TestGatewayTiers.StandardCap - 1, GatewayTiers.Standard)]
    [InlineData(TestGatewayTiers.StandardCap + 1, GatewayTiers.Power)] // one over Standard rounds up to Power
    [InlineData(TestGatewayTiers.PowerCap - 1, GatewayTiers.Power)]
    public void Quota_matching_no_cap_is_enforced_at_the_next_tier_up_and_flagged(long quota, string expectedTier)
    {
        Assert.Equal(new GatewayTierAssignment(expectedTier, true), _mapper.Map(quota));
    }

    [Theory]
    [InlineData(TestGatewayTiers.PowerCap + 1)]
    [InlineData(ValidationConstants.MaxMonthlyTokenQuota)]
    public void Quota_above_every_finite_cap_lands_on_the_largest_finite_tier_flagged(long quota)
    {
        Assert.Equal(new GatewayTierAssignment(GatewayTiers.Power, true), _mapper.Map(quota));
    }

    [Fact]
    public void Tier_order_in_configuration_does_not_matter()
    {
        var options = TestGatewayTiers.Options();
        options.Tiers.Reverse();
        var mapper = new GatewayTierMapper(options);

        Assert.Equal(GatewayTiers.Standard, mapper.Map(TestGatewayTiers.StandardCap).TierProductId);
        Assert.Equal(GatewayTiers.Power, mapper.Map(TestGatewayTiers.StandardCap + 1).TierProductId);
        Assert.Equal(GatewayTiers.Unlimited, mapper.Map(null).TierProductId);
        Assert.Equal([GatewayTiers.Standard, GatewayTiers.Power, GatewayTiers.Unlimited], mapper.Tiers.Select(t => t.ProductId));
    }

    [Fact]
    public void Negative_quota_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _mapper.Map(-1));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(TestGatewayTiers.StandardCap, true)]
    [InlineData(TestGatewayTiers.PowerCap, true)]
    [InlineData(0L, false)]
    [InlineData(1_000_000L, false)]
    [InlineData(TestGatewayTiers.PowerCap + 1, false)]
    public void IsValidQuota_accepts_exactly_unlimited_or_a_configured_cap(long? quota, bool expected)
    {
        Assert.Equal(expected, _mapper.IsValidQuota(quota));
    }

    [Fact]
    public void EnsureValidQuota_throws_ArgumentException_listing_the_allowed_values()
    {
        var exception = Assert.Throws<ArgumentException>(() => _mapper.EnsureValidQuota(1_000_000, "monthlyTokenQuota"));

        Assert.Equal("monthlyTokenQuota", exception.ParamName);
        Assert.Contains("1,000,000 tokens is not a configured budget tier", exception.Message, StringComparison.Ordinal);
        Assert.Contains("5,000,000 (standard)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("20,000,000 (power)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unlimited", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureValidQuota_accepts_unlimited_and_tier_caps()
    {
        _mapper.EnsureValidQuota(null, "q");
        _mapper.EnsureValidQuota(TestGatewayTiers.StandardCap, "q");
        _mapper.EnsureValidQuota(TestGatewayTiers.PowerCap, "q");
    }

    [Fact]
    public void Tiers_lists_finite_tiers_ascending_then_unlimited()
    {
        Assert.Equal([GatewayTiers.Standard, GatewayTiers.Power, GatewayTiers.Unlimited], _mapper.Tiers.Select(t => t.ProductId));
    }

    [Fact]
    public void Constructor_rejects_a_table_with_no_unlimited_tier_or_no_finite_tier()
    {
        var noUnlimited = new GatewayOptions
        {
            Tiers = [new GatewayTier { ProductId = GatewayTiers.Standard, MonthlyTokenQuota = 1 }],
        };
        var noFinite = new GatewayOptions
        {
            Tiers = [new GatewayTier { ProductId = GatewayTiers.Unlimited, MonthlyTokenQuota = 0 }],
        };

        Assert.Throws<ArgumentException>(() => new GatewayTierMapper(noUnlimited));
        Assert.Throws<ArgumentException>(() => new GatewayTierMapper(noFinite));
    }
}
