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
/// <b>Idempotent, but never silently so.</b> The subscription's current product is read first and
/// nothing is written at the gateway when it already matches, so being called for a tier APIM is
/// already enforcing costs one GET. Whether that is a <em>no-op</em> depends on what the database
/// thinks: when the caller's <c>previousTierProductId</c> also matches, the two agree and there is
/// nothing to record; when it does not, an earlier move reached APIM and its save did not land, so the
/// <c>key.tier-changed</c> row is written here (flagged <c>alreadyInPlace</c>) rather than lost. That
/// second case is exactly what a per-user reset failure leaves behind, and returning silently is how
/// it would become permanently unaudited (#211 review). A user with no subscription is skipped:
/// resolution only calls this for a non-empty <see cref="User.ApimSubscriptionId"/>, and the guard
/// makes the contract explicit rather than turning a race into a 404.
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
/// ARM failure propagates rather than leaving the database claiming a tier the gateway is not
/// enforcing. What the caller then does with it differs by caller and both are correct: a request
/// fails outright, while the monthly reset discards that one developer's staged allocation, records
/// the failure and carries on for everybody else (<see cref="QuotaResetService"/>). Registered only
/// when <c>GatewayOptions.IsApimConfigured</c>; otherwise <see cref="NullGatewayTierSync"/> stays in
/// place.
/// </para>
/// </remarks>
public sealed class ApimGatewayTierSync(
    IApimManagementClient apim,
    IAuditWriter audit,
    IGatewayTierSyncActor actor,
    ILogger<ApimGatewayTierSync> logger) : IGatewayTierSync
{
    /// <inheritdoc />
    public async Task SyncAsync(User user, string tierProductId, string? previousTierProductId, CancellationToken cancellationToken)
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
            if (string.Equals(previousTierProductId, productId, StringComparison.OrdinalIgnoreCase))
            {
                // The database and the gateway already agree: nothing moved, nothing to record.
                return;
            }

            // The gateway is on the target product but the database still records a different tier (or
            // none). Something moved this subscription and the row describing it never committed — the
            // residual orphan CONVENTIONS.md's commit-point rule names, which for a reset is a move whose
            // save failed. Returning silently here is how that move becomes permanently unaudited: the
            // caller would go on to write the new tier onto the allocation with nothing explaining it.
            logger.LogWarning(
                "APIM subscription {SubscriptionName} for user {UserId} is already on product {After} while the database still records {Before}; recording the move that was never audited.",
                subscriptionName,
                user.UserId,
                productId,
                previousTierProductId ?? "(none)");

            AddTierChangedRow(actorUser, user, subscriptionName, before: previousTierProductId, after: productId, alreadyInPlace: true);
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
        AddTierChangedRow(actorUser, user, subscriptionName, before: current.ProductId, after: productId, alreadyInPlace: false);

        logger.LogInformation(
            "Moved APIM subscription {SubscriptionName} for user {UserId} from product {Before} to {After}.",
            subscriptionName,
            user.UserId,
            current.ProductId,
            productId);
    }

    /// <summary>
    /// Adds the <c>key.tier-changed</c> row — attributed to the host's actor, or to the system when it
    /// has none — <em>without saving</em>. It joins the caller's change tracker and commits with the
    /// caller's own unit of work, which is what keeps "the gateway moved" and "the audit trail says so"
    /// a single atomic fact (#156 review).
    /// </summary>
    /// <param name="actorUser">The acting user, or <see langword="null"/> for a system-attributed row.</param>
    /// <param name="user">The developer whose subscription the row is about.</param>
    /// <param name="subscriptionName">The APIM subscription name (<c>foundrygate-{UserId}</c>).</param>
    /// <param name="before">The product the row records moving away from — the gateway's own, or the database's when nothing moved here.</param>
    /// <param name="after">The tier product the subscription is on now.</param>
    /// <param name="alreadyInPlace">
    /// <see langword="true"/> when APIM was already on the target product and this row is recording a
    /// move that happened earlier and was never committed, rather than one this call made. An operator
    /// reconciling the trail needs to know which, because only the second has a matching gateway
    /// timestamp.
    /// </param>
    private void AddTierChangedRow(User? actorUser, User user, string subscriptionName, string? before, string after, bool alreadyInPlace)
    {
        var details = new { apimSubscriptionId = user.ApimSubscriptionId, subscriptionName, before, after, alreadyInPlace };
        var targetId = user.UserId.ToString(CultureInfo.InvariantCulture);

        _ = actorUser is null
            ? audit.AddSystem(AuditActions.KeyTierChanged, AuditTargetTypes.ApiKey, targetId, details)
            : audit.Add(actorUser, AuditActions.KeyTierChanged, AuditTargetTypes.ApiKey, targetId, details);
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
