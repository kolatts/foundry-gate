using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ApiManagement;
using Azure.ResourceManager.ApiManagement.Models;
using FoundryGate.Core.Configuration;

namespace FoundryGate.Core.Gateway;

/// <summary>
/// <see cref="IApimManagementClient"/> over <c>Azure.ResourceManager.ApiManagement</c>, addressed by
/// <see cref="GatewayOptions"/> (<c>Gateway__SubscriptionId/ResourceGroup/ApimName</c> from
/// <c>infra/modules/control-plane.bicep</c>) and authenticated with the app's
/// <see cref="TokenCredential"/> — the API identity holds API Management Service Contributor on the
/// instance (<c>control-plane-rbac.bicep</c>). No APIM credentials are ever stored (spec &#167;5).
/// Singleton: <see cref="ArmClient"/> is thread-safe and caches its pipeline.
/// </summary>
/// <remarks>
/// Scope values are written as the <em>full</em> product ARM id (<c>{apim}/products/{id}</c>) because
/// that is the form ARM echoes back, so <see cref="ApimSubscription.Scope"/> compares stably with what
/// this class writes. Live behaviour of the SDK against a real APIM instance is validated manually
/// per the checklist in #132 — the unit suite covers everything above this seam with a fake.
/// </remarks>
public sealed class ArmApimManagementClient : IApimManagementClient
{
    /// <summary>ARM's 404. A literal rather than <c>StatusCodes.Status404NotFound</c>: Core carries no
    /// ASP.NET Core dependency (CONVENTIONS.md &#167;Solution structure) — that is what lets the isolated
    /// Functions worker use this client.</summary>
    private const int NotFoundStatus = 404;

    private readonly ArmClient _armClient;
    private readonly ResourceIdentifier _serviceId;
    private readonly string _subscriptionId;
    private readonly string _resourceGroup;
    private readonly string _apimName;

    public ArmApimManagementClient(GatewayOptions gateway, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(credential);

        if (!gateway.IsApimConfigured)
        {
            throw new ArgumentException("Gateway:SubscriptionId, Gateway:ResourceGroup and Gateway:ApimName must all be set to address APIM.", nameof(gateway));
        }

        _subscriptionId = gateway.SubscriptionId!;
        _resourceGroup = gateway.ResourceGroup!;
        _apimName = gateway.ApimName!;
        _serviceId = ApiManagementServiceResource.CreateResourceIdentifier(_subscriptionId, _resourceGroup, _apimName);
        _armClient = new ArmClient(credential, _subscriptionId);
    }

    /// <inheritdoc />
    public string GetSubscriptionResourceId(string subscriptionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);

        return ApiManagementSubscriptionResource.CreateResourceIdentifier(_subscriptionId, _resourceGroup, _apimName, subscriptionName).ToString();
    }

    /// <inheritdoc />
    public async Task<ApimSubscriptionWithKeys> CreateOrUpdateSubscriptionAsync(string subscriptionName, string displayName, string productId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);

        var content = new ApiManagementSubscriptionCreateOrUpdateContent
        {
            Scope = ProductScope(productId),
            DisplayName = displayName,
            State = SubscriptionState.Active,
        };

        var subscriptions = _armClient.GetApiManagementServiceResource(_serviceId).GetApiManagementSubscriptions();
        var operation = await subscriptions.CreateOrUpdateAsync(WaitUntil.Completed, subscriptionName, content, notify: false, cancellationToken: cancellationToken);
        var data = operation.Value.Data;

        // ARM fills the keys on PUT but not on GET; fall back to listSecrets if this ever changes.
        var keys = data.PrimaryKey is { Length: > 0 } && data.SecondaryKey is { Length: > 0 }
            ? new ApimSubscriptionKeys(data.PrimaryKey, data.SecondaryKey)
            : Map((await operation.Value.GetSecretsAsync(cancellationToken)).Value);

        return new ApimSubscriptionWithKeys(Map(subscriptionName, data), keys);
    }

    /// <inheritdoc />
    public async Task<ApimSubscription?> GetSubscriptionAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);

        var subscriptions = _armClient.GetApiManagementServiceResource(_serviceId).GetApiManagementSubscriptions();
        var response = await subscriptions.GetIfExistsAsync(subscriptionName, cancellationToken);

        return response.HasValue && response.Value is { } resource ? Map(subscriptionName, resource.Data) : null;
    }

    /// <inheritdoc />
    public async Task<ApimSubscriptionKeys> ListSecretsAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);

        try
        {
            var response = await Subscription(subscriptionName).GetSecretsAsync(cancellationToken);
            return Map(response.Value);
        }
        catch (RequestFailedException exception) when (exception.Status == NotFoundStatus)
        {
            throw new ApimSubscriptionNotFoundException(subscriptionName);
        }
    }

    /// <inheritdoc />
    public async Task RegeneratePrimaryKeyAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);

        try
        {
            _ = await Subscription(subscriptionName).RegeneratePrimaryKeyAsync(cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == NotFoundStatus)
        {
            throw new ApimSubscriptionNotFoundException(subscriptionName);
        }
    }

    /// <inheritdoc />
    public async Task RegenerateSecondaryKeyAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);

        try
        {
            _ = await Subscription(subscriptionName).RegenerateSecondaryKeyAsync(cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == NotFoundStatus)
        {
            throw new ApimSubscriptionNotFoundException(subscriptionName);
        }
    }

    /// <inheritdoc />
    public async Task UpdateScopeAsync(string subscriptionName, string productId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);

        var patch = new ApiManagementSubscriptionPatch { Scope = ProductScope(productId) };

        try
        {
            _ = await Subscription(subscriptionName).UpdateAsync(ETag.All, patch, notify: false, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == NotFoundStatus)
        {
            throw new ApimSubscriptionNotFoundException(subscriptionName);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSubscriptionAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);

        try
        {
            _ = await Subscription(subscriptionName).DeleteAsync(WaitUntil.Completed, ETag.All, cancellationToken);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status == NotFoundStatus)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetNamedValueAsync(string namedValueName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namedValueName);

        var namedValues = _armClient.GetApiManagementServiceResource(_serviceId).GetApiManagementNamedValues();
        var response = await namedValues.GetIfExistsAsync(namedValueName, cancellationToken);

        if (!response.HasValue || response.Value is not { } resource)
        {
            return null;
        }

        // listValue, not the data off the GET: ARM omits properties.value for a secret named value,
        // so reading the resource would be right only for the non-secret half of the contract.
        var secret = await resource.GetValueAsync(cancellationToken);
        return secret.Value?.Value;
    }

    /// <inheritdoc />
    public async Task SetNamedValueAsync(string namedValueName, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namedValueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var content = new ApiManagementNamedValueCreateOrUpdateContent
        {
            // Must equal the ARM name: {{token}} in a policy resolves against displayName.
            DisplayName = namedValueName,
            Value = value,
            IsSecret = false,
        };

        var namedValues = _armClient.GetApiManagementServiceResource(_serviceId).GetApiManagementNamedValues();
        _ = await namedValues.CreateOrUpdateAsync(WaitUntil.Completed, namedValueName, content, ifMatch: ETag.All, cancellationToken: cancellationToken);
    }

    private ApiManagementSubscriptionResource Subscription(string subscriptionName) =>
        _armClient.GetApiManagementSubscriptionResource(
            ApiManagementSubscriptionResource.CreateResourceIdentifier(_subscriptionId, _resourceGroup, _apimName, subscriptionName));

    private string ProductScope(string productId) => $"{_serviceId}/products/{productId}";

    private ApimSubscription Map(string subscriptionName, SubscriptionContractData data)
    {
        var scope = data.Scope ?? string.Empty;
        var resourceId = data.Id?.ToString()
            ?? ApiManagementSubscriptionResource.CreateResourceIdentifier(_subscriptionId, _resourceGroup, _apimName, subscriptionName).ToString();

        return new ApimSubscription(
            subscriptionName,
            resourceId,
            data.DisplayName ?? string.Empty,
            scope,
            ProductIdFromScope(scope),
            data.State?.ToString() ?? string.Empty);
    }

    private static ApimSubscriptionKeys Map(SubscriptionKeysContract keys) =>
        new(keys.PrimaryKey ?? string.Empty, keys.SecondaryKey ?? string.Empty);

    /// <summary>The segment after <c>/products/</c> in a scope id; <see langword="null"/> for non-product scopes (<c>/apis</c>, a single API, …).</summary>
    public static string? ProductIdFromScope(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        const string Marker = "/products/";
        var index = scope.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var productId = scope[(index + Marker.Length)..].Trim('/');
        return productId.Length == 0 ? null : productId;
    }
}
