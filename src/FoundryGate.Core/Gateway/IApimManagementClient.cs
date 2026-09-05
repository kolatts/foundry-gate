namespace FoundryGate.Core.Gateway;

/// <summary>
/// The thin seam over the APIM management plane (<c>Azure.ResourceManager.ApiManagement</c>) for
/// subscription lifecycle: exactly the seven operations the control plane needs, in
/// FoundryGate's vocabulary (subscription <em>name</em> = <c>foundrygate-{UserId}</c>, product =
/// quota-tier id), so its callers are testable with an in-memory fake and the ARM SDK's resource
/// graph stays in one class (<see cref="ArmApimManagementClient"/>). No live Azure in tests.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>FoundryGate.Core</c>, not the Api (#194): two hosts need it. The Api composes it into
/// <c>ApimKeyService</c> (provision / rotate / reveal / revoke, plus key protection); the Functions
/// host needs only <see cref="UpdateScopeAsync"/>, so that the monthly reset can move a developer's
/// subscription to the tier product their quota resolved to instead of logging that it could not
/// (<see cref="Quota.ApimGatewayTierSync"/>). Nothing here touches key <em>protection</em> — that
/// stays in the Api, which is the only host that stores or reveals a developer's key.
/// </para>
/// <para>
/// Error contract: a subscription that does not exist is <see langword="null"/> from
/// <see cref="GetSubscriptionAsync"/>, <see langword="false"/> from <see cref="DeleteSubscriptionAsync"/>,
/// and <see cref="ApimSubscriptionNotFoundException"/> from every operation that needs it to exist.
/// Other ARM failures (auth, throttling, 5xx) surface as <c>Azure.RequestFailedException</c> — an
/// unmapped 500 for the caller, which is correct: they are operational faults, not caller errors.
/// </para>
/// </remarks>
public interface IApimManagementClient
{
    /// <summary>
    /// The ARM resource id a subscription named <paramref name="subscriptionName"/> has (or would have)
    /// on the configured APIM instance — deterministic, no call made. The key service writes it into
    /// <c>User.ApimSubscriptionId</c> as the provisioning claim <em>before</em> the APIM PUT.
    /// </summary>
    string GetSubscriptionResourceId(string subscriptionName);

    /// <summary>
    /// Creates (or, if a subscription with this name exists, re-PUTs) an <c>active</c> subscription
    /// scoped to <c>/products/{productId}</c> and returns it with both of its keys — the one call
    /// that returns key material without a separate secrets read.
    /// </summary>
    /// <param name="subscriptionName">ARM name (<c>sid</c>), from <c>ApimSubscriptionNames.ForUser</c>.</param>
    /// <param name="displayName">Human label shown in the APIM portal (≤ 100 chars; APIM's limit).</param>
    /// <param name="productId">Quota-tier product id (a <c>GatewayTiers</c> value).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ApimSubscriptionWithKeys> CreateOrUpdateSubscriptionAsync(string subscriptionName, string displayName, string productId, CancellationToken cancellationToken);

    /// <summary>The subscription's metadata (no key material), or <see langword="null"/> when it does not exist — the orphan-detection probe plan 21 relies on.</summary>
    Task<ApimSubscription?> GetSubscriptionAsync(string subscriptionName, CancellationToken cancellationToken);

    /// <summary>Both current keys (APIM's <c>listSecrets</c>).</summary>
    /// <exception cref="ApimSubscriptionNotFoundException">No such subscription.</exception>
    Task<ApimSubscriptionKeys> ListSecretsAsync(string subscriptionName, CancellationToken cancellationToken);

    /// <summary>Invalidates the primary key and mints a new one (APIM returns no body; follow with <see cref="ListSecretsAsync"/>).</summary>
    /// <exception cref="ApimSubscriptionNotFoundException">No such subscription.</exception>
    Task RegeneratePrimaryKeyAsync(string subscriptionName, CancellationToken cancellationToken);

    /// <summary>Invalidates the secondary key and mints a new one. FoundryGate never issues the secondary; regenerating it alongside the primary bounds its lifetime (#117).</summary>
    /// <exception cref="ApimSubscriptionNotFoundException">No such subscription.</exception>
    Task RegenerateSecondaryKeyAsync(string subscriptionName, CancellationToken cancellationToken);

    /// <summary>Re-scopes the subscription to <c>/products/{productId}</c> — how a developer changes quota tier (#82). Keys are unchanged.</summary>
    /// <exception cref="ApimSubscriptionNotFoundException">No such subscription.</exception>
    Task UpdateScopeAsync(string subscriptionName, string productId, CancellationToken cancellationToken);

    /// <summary>Deletes the subscription (the key stops working immediately). <see langword="false"/> when it was already gone — deletion is idempotent.</summary>
    Task<bool> DeleteSubscriptionAsync(string subscriptionName, CancellationToken cancellationToken);

    /// <summary>
    /// The current value of an APIM <em>named value</em>, or <see langword="null"/> when no named value
    /// of that name exists. The gateway's per-tier model alias maps live in
    /// <c>fg-model-map-{tier}</c> (plans/25, #86), which is why this pair exists at all: the alias map
    /// is the allowlist, and editing it is a control-plane operation that must not require a policy
    /// redeploy.
    /// </summary>
    /// <remarks>
    /// Read through APIM's <c>listValue</c> operation rather than the resource GET: ARM does not fill
    /// <c>properties.value</c> on a GET for secret named values, and depending on which of the two
    /// shapes comes back would make the read correct only for the non-secret case.
    /// </remarks>
    /// <param name="namedValueName">ARM name of the named value, e.g. <c>fg-model-map-standard</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> GetNamedValueAsync(string namedValueName, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or replaces a non-secret named value. <c>displayName</c> is set to the same string as
    /// the ARM name, because that is what a policy's <c>{{token}}</c> resolves against
    /// (<c>infra/modules/ai-gateway.bicep</c> does the same) — a mismatch would leave the policy
    /// reading a token that no longer exists.
    /// </summary>
    /// <param name="namedValueName">ARM name of the named value.</param>
    /// <param name="value">The new value. APIM rejects an empty or whitespace-only value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetNamedValueAsync(string namedValueName, string value, CancellationToken cancellationToken);
}

/// <summary>A subscription as the management plane reports it, minus key material.</summary>
/// <param name="Name">ARM name (<c>sid</c>).</param>
/// <param name="ResourceId">Full ARM resource id — what <c>User.ApimSubscriptionId</c> stores.</param>
/// <param name="DisplayName">Portal label.</param>
/// <param name="Scope">Full scope ARM id (<c>.../products/{productId}</c>).</param>
/// <param name="ProductId">The product segment of <paramref name="Scope"/>, or <see langword="null"/> when the scope is not a product (e.g. all-APIs).</param>
/// <param name="State">APIM subscription state (<c>active</c>, <c>suspended</c>, …), lower-case as ARM reports it.</param>
public sealed record ApimSubscription(string Name, string ResourceId, string DisplayName, string Scope, string? ProductId, string State);

/// <summary>Both keys of a subscription. Only <see cref="PrimaryKey"/> is ever issued to a developer.</summary>
public sealed record ApimSubscriptionKeys(string PrimaryKey, string SecondaryKey);

/// <summary>Result of <see cref="IApimManagementClient.CreateOrUpdateSubscriptionAsync"/>.</summary>
public sealed record ApimSubscriptionWithKeys(ApimSubscription Subscription, ApimSubscriptionKeys Keys);

/// <summary>
/// An operation needed an APIM subscription that does not exist — the database says a user has a key
/// but the management plane has no such subscription (deleted in the portal, or a different APIM
/// instance is configured). Deliberately not <see cref="KeyNotFoundException"/>: that would map to a
/// 404 "resource missing", whereas this is a state conflict the key service turns into a 409 with
/// "revoke and re-provision" guidance.
/// </summary>
public sealed class ApimSubscriptionNotFoundException(string subscriptionName)
    : Exception($"APIM subscription '{subscriptionName}' does not exist on the configured APIM instance.")
{
    /// <summary>The subscription name that was looked up.</summary>
    public string SubscriptionName { get; } = subscriptionName;
}
