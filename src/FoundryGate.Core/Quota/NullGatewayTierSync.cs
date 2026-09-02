using FoundryGate.Data.Entities;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Core.Quota;

/// <summary>
/// The <see cref="IGatewayTierSync"/> for a host with no gateway to talk to: records the intended move
/// at Debug and touches nothing. Both hosts register it when
/// <see cref="Configuration.GatewayOptions.IsApimConfigured"/> is false — the <c>local</c> shape, and
/// a fork running the control plane without APIM addressing — where
/// <see cref="ApimGatewayTierSync"/> would have nothing to call. While this is registered a quota
/// change is recorded in <c>QuotaAllocation</c> but <b>not enforced</b> at any gateway, which is
/// exactly true rather than a stopgap: there is no gateway to enforce it.
/// </summary>
public sealed class NullGatewayTierSync(ILogger<NullGatewayTierSync> logger) : IGatewayTierSync
{
    /// <inheritdoc />
    public Task SyncAsync(User user, string tierProductId, string? previousTierProductId, CancellationToken cancellationToken)
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
