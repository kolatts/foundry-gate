using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Api.Services.Quota;

/// <summary>
/// The numeric-quota → tier-product rule (#7 direction update), pinned at its boundaries: exactly at
/// a cap fits that tier, one over rolls to the next, above every finite cap lands on the largest
/// finite tier <em>flagged</em> capped, and unlimited is the unlimited product.
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
    [InlineData(0L, GatewayTiers.Standard)]
    [InlineData(1L, GatewayTiers.Standard)]
    [InlineData(TestGatewayTiers.StandardCap, GatewayTiers.Standard)] // exactly at the cap fits
    [InlineData(TestGatewayTiers.StandardCap + 1, GatewayTiers.Power)] // one over rolls up
    [InlineData(TestGatewayTiers.PowerCap, GatewayTiers.Power)]
    public void Finite_quota_maps_to_the_smallest_tier_whose_cap_covers_it(long quota, string expectedTier)
    {
        Assert.Equal(new GatewayTierAssignment(expectedTier, false), _mapper.Map(quota));
    }

    [Theory]
    [InlineData(TestGatewayTiers.PowerCap + 1)]
    [InlineData(ValidationConstants.MaxMonthlyTokenQuota)]
    public void Quota_above_every_finite_cap_lands_on_the_largest_finite_tier_flagged_capped(long quota)
    {
        Assert.Equal(new GatewayTierAssignment(GatewayTiers.Power, true), _mapper.Map(quota));
    }

    [Fact]
    public void Tier_order_in_configuration_does_not_matter()
    {
        var options = TestGatewayTiers.Options();
        options.Tiers.Reverse();
        var mapper = new GatewayTierMapper(options);

        Assert.Equal(GatewayTiers.Standard, mapper.Map(1_000).TierProductId);
        Assert.Equal(GatewayTiers.Power, mapper.Map(TestGatewayTiers.StandardCap + 1).TierProductId);
        Assert.Equal(GatewayTiers.Unlimited, mapper.Map(null).TierProductId);
    }

    [Fact]
    public void Negative_quota_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _mapper.Map(-1));
    }

    [Fact]
    public void Constructor_rejects_a_table_with_no_unlimited_tier_or_no_finite_tier()
    {
        var noUnlimited = new GatewayTierOptions
        {
            Tiers = [new GatewayTier { ProductId = GatewayTiers.Standard, MonthlyTokenQuota = 1 }],
        };
        var noFinite = new GatewayTierOptions
        {
            Tiers = [new GatewayTier { ProductId = GatewayTiers.Unlimited, MonthlyTokenQuota = 0 }],
        };

        Assert.Throws<ArgumentException>(() => new GatewayTierMapper(noUnlimited));
        Assert.Throws<ArgumentException>(() => new GatewayTierMapper(noFinite));
    }
}
