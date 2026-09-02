using FoundryGate.Api.Services.Keys;
using FoundryGate.Data.Entities;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// The real <see cref="IGatewayTierSync"/> (#118): puts a developer's APIM subscription on the tier
/// product their quota resolved to, by delegating to <see cref="IApimKeyService.MoveToProductAsync"/> —
/// a re-scope of the existing subscription (<c>PUT .../subscriptions/{sid}</c> with
/// <c>scope = /products/{tier}</c>), which leaves the developer's key untouched so nobody has to
/// reconfigure a CLI because their budget changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent.</b> <see cref="IApimKeyService.MoveToProductAsync"/> reads the subscription's current
/// product first and returns without a write when it already matches, so being called for a tier the
/// gateway is already enforcing (which happens whenever the "previous tier" has to be inferred from
/// allocation history) costs one GET and changes nothing. A user with no subscription is skipped here:
/// resolution only calls this for a non-empty <c>ApimSubscriptionId</c>, and the guard makes the
/// contract explicit rather than turning a race into a 404.
/// </para>
/// <para>
/// <b>No audit row of its own.</b> <c>MoveToProductAsync</c> already writes <c>key.tier-changed</c> with
/// the before/after product ids and saves it; writing another here would double-count every tier change
/// in the audit trail.
/// </para>
/// <para>
/// <b>Failure is the caller's failure.</b> Resolution calls this <em>before</em> its own
/// <c>SaveChangesAsync</c>, so an ARM failure propagates and the request fails rather than leaving the
/// database claiming a tier the gateway is not enforcing. Registered only when
/// <c>GatewayOptions.IsApimConfigured</c>; otherwise <see cref="NullGatewayTierSync"/> stays in place.
/// </para>
/// </remarks>
public sealed class ApimGatewayTierSync(IApimKeyService keys, ILogger<ApimGatewayTierSync> logger) : IGatewayTierSync
{
    /// <inheritdoc />
    public async Task SyncAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(tierProductId);

        if (string.IsNullOrEmpty(user.ApimSubscriptionId))
        {
            logger.LogDebug("User {UserId} has no APIM subscription; nothing to move to tier product {TierProductId}.", user.UserId, tierProductId);
            return;
        }

        await keys.MoveToProductAsync(user, tierProductId, cancellationToken);
    }
}
