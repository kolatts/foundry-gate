using FoundryGate.Data.Entities;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// The <see cref="IGatewayTierSync"/> for a host with no gateway to talk to: records the intended move
/// at Debug and touches nothing. Registered by
/// <see cref="QuotaServiceCollectionExtensions.AddQuotaServices"/> when
/// <c>GatewayOptions.IsApimConfigured</c> is false — the <c>local</c> shape, where
/// <see cref="ApimGatewayTierSync"/> would have nothing to call. While this is registered a quota
/// change is recorded in <c>QuotaAllocation</c> but <b>not enforced</b> at any gateway.
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
