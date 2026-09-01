using FoundryGate.Data.Entities;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// Placeholder <see cref="IGatewayTierSync"/> that records the intended move at Debug and touches
/// nothing. Registered until <c>ApimGatewayTierSync</c> (#118 — move the APIM subscription to the
/// user's resolved tier product via the Management plane) replaces it in
/// <see cref="QuotaServiceCollectionExtensions.AddQuotaServices"/>. While this is registered a quota
/// change is recorded in <c>QuotaAllocation</c> but <b>not enforced</b> at the gateway beyond the
/// tier the subscription was originally issued on.
/// </summary>
public sealed class NullGatewayTierSync(ILogger<NullGatewayTierSync> logger) : IGatewayTierSync
{
    /// <inheritdoc />
    public Task SyncAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(tierProductId);

        logger.LogDebug(
            "Gateway tier sync is not implemented (#118): user {UserId} would move to tier product {TierProductId}.",
            user.UserId,
            tierProductId);

        return Task.CompletedTask;
    }
}
