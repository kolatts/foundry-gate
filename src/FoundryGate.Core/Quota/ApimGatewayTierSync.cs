using System.Globalization;
using Azure;
using FoundryGate.Core.Gateway;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Core.Quota;

/// <summary>
/// The real <see cref="IGatewayTierSync"/> (#118, moved to Core by #194): puts a developer's APIM
/// subscription on the tier product their quota resolved to by re-scoping the existing subscription
/// (<c>PATCH .../subscriptions/{sid}</c> with <c>scope = /products/{tier}</c>), which leaves their key
/// untouched so nobody has to reconfigure a CLI because their budget changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both hosts run this one.</b> It composes <see cref="IApimManagementClient"/> directly rather
/// than the Api's key service, so the Functions host — which resolves quota on the monthly reset but
/// has no key service, no key protection and no HTTP caller — can act on a tier change instead of
/// logging that it could not (#194 replaced the stopgap <c>WarningGatewayTierSync</c> with this).
/// The one thing that genuinely differs between the hosts is who the audit row belongs to, and that
/// is the <see cref="IGatewayTierSyncActor"/> seam.
/// </para>
/// <para>
/// <b>Idempotent.</b> The subscription's current product is read first and nothing is written when it
/// already matches, so being called for a tier the gateway is already enforcing (which happens
/// whenever the "previous tier" has to be inferred from allocation history) costs one GET and changes
/// nothing. A user with no subscription is skipped: resolution only calls this for a non-empty
/// <see cref="User.ApimSubscriptionId"/>, and the guard makes the contract explicit rather than
/// turning a race into a 404.
/// </para>
/// <para>
/// <b>Audited, added not saved.</b> The <c>key.tier-changed</c> row joins the caller's change tracker
/// and commits with the caller's own <c>SaveChangesAsync</c> — that is what keeps "the gateway moved"
/// and "the audit trail says so" a single atomic fact (#156 review). A caller that does not save is a
/// bug: the gateway would be enforcing a tier the audit trail never recorded. Because APIM has already
/// accepted the move by then, that save must run on <see cref="CancellationToken.None"/>; quota
/// resolution expresses exactly that with <c>CommitToken.For(TierSyncRequested, …)</c>.
/// </para>
/// <para>
/// <b>Failure is the caller's failure.</b> Resolution calls this <em>before</em> its own save, so an
/// ARM failure propagates and the request (or the reset) fails rather than leaving the database
/// claiming a tier the gateway is not enforcing. Registered only when
/// <c>GatewayOptions.IsApimConfigured</c>; otherwise <see cref="NullGatewayTierSync"/> stays in place.
/// </para>
/// </remarks>
public sealed class ApimGatewayTierSync(
    IApimManagementClient apim,
    IAuditWriter audit,
    IGatewayTierSyncActor actor,
    ILogger<ApimGatewayTierSync> logger) : IGatewayTierSync
{
    /// <inheritdoc />
    public async Task SyncAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var productId = NormalizeTier(tierProductId);

        if (string.IsNullOrEmpty(user.ApimSubscriptionId))
        {
            logger.LogDebug("User {UserId} has no APIM subscription; nothing to move to tier product {TierProductId}.", user.UserId, tierProductId);
            return;
        }

        // The actor before the gateway: an implementation that refuses (the Api's, for a caller with no
        // User row) must refuse before the subscription has moved, not after (CONVENTIONS.md §External
        // side effects have a commit point).
        var actorUser = await actor.ResolveActorAsync(cancellationToken);

        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        var current = await UpstreamAsync(user, productId, () => apim.GetSubscriptionAsync(subscriptionName, cancellationToken))
            ?? throw SubscriptionMissing(user, new ApimSubscriptionNotFoundException(subscriptionName));

        if (string.Equals(current.ProductId, productId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _ = await UpstreamAsync<object?>(user, productId, async () =>
            {
                await apim.UpdateScopeAsync(subscriptionName, productId, cancellationToken);
                return null;
            });
        }
        catch (ApimSubscriptionNotFoundException exception)
        {
            throw SubscriptionMissing(user, exception);
        }

        // ---- commit point: APIM has re-scoped the subscription. ----
        var details = new { apimSubscriptionId = user.ApimSubscriptionId, subscriptionName, before = current.ProductId, after = productId };
        var targetId = user.UserId.ToString(CultureInfo.InvariantCulture);

        _ = actorUser is null
            ? audit.AddSystem(AuditActions.KeyTierChanged, AuditTargetTypes.ApiKey, targetId, details)
            : audit.Add(actorUser, AuditActions.KeyTierChanged, AuditTargetTypes.ApiKey, targetId, details);

        logger.LogInformation(
            "Moved APIM subscription {SubscriptionName} for user {UserId} from product {Before} to {After}.",
            subscriptionName,
            user.UserId,
            current.ProductId,
            productId);
    }

    /// <summary>
    /// Runs an ARM call, turning a dependency fault into <see cref="UpstreamDependencyException"/> (502).
    /// Without it the SDK's <see cref="RequestFailedException"/> (a missing role, a 429, a 500) would
    /// escape the Api's handler as a bare 500 on <c>PUT /users/{id}/quota</c>,
    /// <c>POST /users/{id}/activate</c> and request approval — all three of which document 502 (#156
    /// review). No "nothing was saved" claim: the caller has already mutated its own rows in memory,
    /// and what is true is that it will not commit them, because this aborts the caller before its save.
    /// </summary>
    private static async Task<T> UpstreamAsync<T>(User user, string productId, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when (IsUpstreamFailure(exception))
        {
            throw new UpstreamDependencyException(
                $"The API Management gateway did not accept moving user {user.UserId}'s subscription to the '{productId}' tier, so the quota change was not applied. Please retry.",
                exception);
        }
    }

    /// <summary>
    /// The dependency-failed types (allowlist, matching <c>UserLifecycleService</c>): an ARM/transport
    /// fault is the gateway's fault. <see cref="ApimSubscriptionNotFoundException"/> is deliberately
    /// absent — a subscription the database claims and the gateway does not have is a state conflict
    /// (409 with "revoke and re-provision" guidance), not a dependency outage.
    /// </summary>
    private static bool IsUpstreamFailure(Exception exception) =>
        exception is RequestFailedException
            or HttpRequestException
            or TimeoutException;

    private static ConflictException SubscriptionMissing(User user, ApimSubscriptionNotFoundException inner) =>
        new(
            $"The APIM subscription behind user {user.UserId}'s key no longer exists on the gateway (deleted outside FoundryGate?). " +
            $"Revoke the key (DELETE /keys/{user.UserId}) and provision a new one.",
            inner);

    /// <summary>Validates against <see cref="GatewayTiers.All"/>; tier ids are lower-case product ids, so the comparison is case-insensitive and the result normalized.</summary>
    private static string NormalizeTier(string tierProductId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tierProductId);

        var match = GatewayTiers.All.FirstOrDefault(tier => string.Equals(tier, tierProductId.Trim(), StringComparison.OrdinalIgnoreCase));
        return match
            ?? throw new ArgumentException($"'{tierProductId}' is not a gateway tier. Valid tiers: {string.Join(", ", GatewayTiers.All)}.", nameof(tierProductId));
    }
}
