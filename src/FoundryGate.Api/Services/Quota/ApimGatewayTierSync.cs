using Azure;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Core.Quota;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Exceptions;

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
/// <b>No audit row of its own.</b> <c>MoveToProductAsync</c> already adds <c>key.tier-changed</c> with
/// the before/after product ids; writing another here would double-count every tier change in the audit
/// trail. That row is <em>added, not saved</em> — it commits with the calling unit of work, which is what
/// keeps "the gateway moved" and "the audit trail says so" a single atomic fact (#156 review).
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

        try
        {
            await keys.MoveToProductAsync(user, tierProductId, cancellationToken);
        }
        catch (Exception exception) when (IsUpstreamFailure(exception))
        {
            // Without this the ARM SDK's RequestFailedException (a missing role, a 429, a 500) escapes
            // the handler as a bare 500 on PUT /users/{id}/quota, POST /users/{id}/activate and request
            // approval — all three of which document 502 (#156 review). No "nothing was saved" claim
            // here: the caller has already mutated its own rows in memory, and what is true is that it
            // will not commit them, because this exception aborts the caller before its save.
            throw new UpstreamDependencyException(
                $"The API Management gateway did not accept moving user {user.UserId}'s subscription to the '{tierProductId}' tier, so the quota change was not applied. Please retry.",
                exception);
        }
    }

    /// <summary>
    /// The dependency-failed types (allowlist, matching <c>UserLifecycleService</c>): an ARM/transport
    /// fault is the gateway's fault, while the key service's own <see cref="KeyNotFoundException"/> /
    /// <see cref="ConflictException"/> / <see cref="ArgumentException"/> keep their 404/409/400 mapping
    /// and a genuine bug keeps its 500.
    /// </summary>
    private static bool IsUpstreamFailure(Exception exception) =>
        exception is RequestFailedException
            or ApimSubscriptionNotFoundException
            or HttpRequestException
            or TimeoutException;
}
