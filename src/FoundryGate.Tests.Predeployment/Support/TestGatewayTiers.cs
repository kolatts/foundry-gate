using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// A <see cref="GatewayOptions"/> carrying only the tier table as shipped in <c>appsettings.json</c> /
/// <c>infra/main.bicep</c> (Standard 5M, Power 20M, Unlimited) and no gateway addressing — the local
/// shape. For service-level tests that construct <see cref="GatewayTierMapper"/> directly; endpoint
/// tests get the same values through the real <c>appsettings.json</c>.
/// </summary>
public static class TestGatewayTiers
{
    public const long StandardCap = 5_000_000;
    public const long PowerCap = 20_000_000;

    public static GatewayOptions Options() => new()
    {
        Tiers =
        [
            new GatewayTier { ProductId = GatewayTiers.Standard, DisplayName = "Standard", MonthlyTokenQuota = StandardCap },
            new GatewayTier { ProductId = GatewayTiers.Power, DisplayName = "Power", MonthlyTokenQuota = PowerCap },
            new GatewayTier { ProductId = GatewayTiers.Unlimited, DisplayName = "Unlimited", MonthlyTokenQuota = 0 },
        ],
    };

    public static GatewayTierMapper Mapper() => new(Options());
}
