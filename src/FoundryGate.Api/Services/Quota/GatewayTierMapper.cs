using FoundryGate.Api.Configuration;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// Maps a resolved numeric quota onto the APIM tier product the gateway will actually enforce
/// (issue #7 direction update: enforcement is <c>llm-token-limit</c> on tier <em>products</em>).
/// Pure and singleton — the tier table comes from <see cref="GatewayTierOptions"/> once at startup.
/// </summary>
/// <remarks>
/// Rules, in order: unlimited (<see langword="null"/>) → the unlimited tier; otherwise the smallest
/// finite tier whose cap is ≥ the quota (a quota exactly at a cap fits that tier); a quota above every
/// finite cap → the <em>largest</em> finite tier, flagged <see cref="GatewayTierAssignment.IsGatewayCapped"/>
/// because the gateway will 403 at that tier's cap, below the numeric quota.
/// </remarks>
public sealed class GatewayTierMapper
{
    private readonly IReadOnlyList<GatewayTier> _finiteTiersAscending;
    private readonly GatewayTier _unlimitedTier;

    /// <summary>Creates a mapper over <paramref name="options"/>, which must already have passed validation.</summary>
    public GatewayTierMapper(GatewayTierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _finiteTiersAscending = [.. options.Tiers.Where(t => !t.IsUnlimited).OrderBy(t => t.MonthlyTokenQuota)];
        _unlimitedTier = options.Tiers.SingleOrDefault(t => t.IsUnlimited)
            ?? throw new ArgumentException("Gateway:Tiers must contain exactly one unlimited tier (MonthlyTokenQuota = 0).", nameof(options));

        if (_finiteTiersAscending.Count == 0)
        {
            throw new ArgumentException("Gateway:Tiers must contain at least one finite tier (MonthlyTokenQuota > 0).", nameof(options));
        }
    }

    /// <summary>The tier product for <paramref name="resolvedQuota"/> (<see langword="null"/> = unlimited).</summary>
    public GatewayTierAssignment Map(long? resolvedQuota)
    {
        if (resolvedQuota is null)
        {
            return new GatewayTierAssignment(_unlimitedTier.ProductId, IsGatewayCapped: false);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(resolvedQuota.Value);

        foreach (var tier in _finiteTiersAscending)
        {
            if (tier.MonthlyTokenQuota >= resolvedQuota.Value)
            {
                return new GatewayTierAssignment(tier.ProductId, IsGatewayCapped: false);
            }
        }

        return new GatewayTierAssignment(_finiteTiersAscending[^1].ProductId, IsGatewayCapped: true);
    }
}

/// <summary>Output of <see cref="GatewayTierMapper.Map"/>.</summary>
/// <param name="TierProductId">The APIM product id (one of <see cref="Domain.Constants.GatewayTiers.All"/>).</param>
/// <param name="IsGatewayCapped">True when the numeric quota exceeded every finite cap and the gateway will stop the developer at <paramref name="TierProductId"/>'s cap instead.</param>
public readonly record struct GatewayTierAssignment(string TierProductId, bool IsGatewayCapped);
