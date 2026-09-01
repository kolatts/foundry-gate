namespace FoundryGate.Api.Services.Keys;

/// <summary>
/// The <see cref="IApimManagementClient"/> registered when <c>Gateway:SubscriptionId/ResourceGroup/
/// ApimName</c> are absent — permitted in the <c>local</c> environment only (a cloud host without them
/// fails at startup; see <see cref="KeysServiceCollectionExtensions"/>). Every call throws the same
/// explanatory <see cref="InvalidOperationException"/> (→ 500, logged), so a developer running the
/// Api against docker SQL with no Azure gets "APIM is not configured" from the key endpoints and
/// everything else works.
/// </summary>
public sealed class UnconfiguredApimManagementClient : IApimManagementClient
{
    private const string Message =
        "APIM is not configured: set Gateway:SubscriptionId, Gateway:ResourceGroup and Gateway:ApimName " +
        "(infra sets Gateway__* on the Container App) to enable APIM subscription-key operations.";

    /// <inheritdoc />
    public Task<ApimSubscriptionWithKeys> CreateOrUpdateSubscriptionAsync(string subscriptionName, string displayName, string productId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<ApimSubscription?> GetSubscriptionAsync(string subscriptionName, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<ApimSubscriptionKeys> ListSecretsAsync(string subscriptionName, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task RegeneratePrimaryKeyAsync(string subscriptionName, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task RegenerateSecondaryKeyAsync(string subscriptionName, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task UpdateScopeAsync(string subscriptionName, string productId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public Task<bool> DeleteSubscriptionAsync(string subscriptionName, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(Message);
}
