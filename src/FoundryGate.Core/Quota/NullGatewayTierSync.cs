using FoundryGate.Data.Entities;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Core.Quota;

/// <summary>
/// The <see cref="IGatewayTierSync"/> for a host with no gateway to talk to: records the intended move
/// at Debug and touches nothing. The Api registers it (<c>AddQuotaServices</c>) when
/// <see cref="Configuration.GatewayOptions.IsApimConfigured"/> is false — the <c>local</c> shape, where
/// <c>ApimGatewayTierSync</c> would have nothing to call; the Functions host registers it
/// unconditionally, because the monthly reset never moves a subscription between products (a reset
/// re-runs resolution over unchanged inputs, so the tier it resolves is the tier already recorded).
/// While this is registered a quota change is recorded in <c>QuotaAllocation</c> but <b>not
/// enforced</b> at any gateway.
/// </summary>
public sealed class NullGatewayTierSync(ILogger<NullGatewayTierSync> logger) : IGatewayTierSync
{
    /// <inheritdoc />
    public Task SyncAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(tierProductId);

        logger.LogDebug(
            "No gateway is configured (Gateway:SubscriptionId/ResourceGroup/ApimName): user {UserId} would move to tier product {TierProductId}.",
            user.UserId,
            tierProductId);

        return Task.CompletedTask;
    }
}
