using System.Security.Cryptography;
using FoundryGate.Api.Services.Keys;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// In-memory <see cref="IApimManagementClient"/>: a dictionary of subscriptions with random 32-hex
/// keys, the same not-found contract as the ARM implementation (null / false /
/// <see cref="ApimSubscriptionNotFoundException"/>), and a call log for assertions. Registered by
/// <c>ApiTestFactory</c> in place of the ARM client so no test reaches Azure. Hand-rolled per
/// CONVENTIONS.md (no mocking library).
/// </summary>
public sealed class FakeApimManagementClient : IApimManagementClient
{
    /// <summary>The fake APIM instance's ARM id; resource ids and scopes are built under it exactly as ARM would.</summary>
    public const string ServiceId =
        "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-foundrygate-test/providers/Microsoft.ApiManagement/service/apim-foundrygate-test";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _subscriptions = new(StringComparer.Ordinal);
    private readonly List<string> _calls = [];

    /// <summary>Every call, as <c>"{Method}:{subscriptionName}"</c> (plus <c>:{productId}</c> where relevant), in order.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>When set, <see cref="CreateOrUpdateSubscriptionAsync"/> throws it instead of creating — simulates an ARM failure.</summary>
    public Exception? ThrowOnCreate { get; set; }

    /// <summary>Pre-creates a subscription (an "orphan" from the key service's point of view) and returns its current keys.</summary>
    public ApimSubscriptionKeys Seed(string subscriptionName, string productId, string displayName = "orphan")
    {
        lock (_gate)
        {
            var entry = new Entry(displayName, productId, NewKey(), NewKey());
            _subscriptions[subscriptionName] = entry;
            return new ApimSubscriptionKeys(entry.PrimaryKey, entry.SecondaryKey);
        }
    }

    /// <summary>Removes a subscription behind the key service's back — simulates a portal deletion.</summary>
    public bool Remove(string subscriptionName)
    {
        lock (_gate)
        {
            return _subscriptions.Remove(subscriptionName);
        }
    }

    public bool Contains(string subscriptionName)
    {
        lock (_gate)
        {
            return _subscriptions.ContainsKey(subscriptionName);
        }
    }

    /// <summary>The current keys without going through the interface (no call is logged).</summary>
    public ApimSubscriptionKeys KeysOf(string subscriptionName)
    {
        lock (_gate)
        {
            var entry = _subscriptions[subscriptionName];
            return new ApimSubscriptionKeys(entry.PrimaryKey, entry.SecondaryKey);
        }
    }

    /// <summary>The current product id without going through the interface (no call is logged).</summary>
    public string ProductOf(string subscriptionName)
    {
        lock (_gate)
        {
            return _subscriptions[subscriptionName].ProductId;
        }
    }

    /// <inheritdoc />
    public Task<ApimSubscriptionWithKeys> CreateOrUpdateSubscriptionAsync(string subscriptionName, string displayName, string productId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"CreateOrUpdate:{subscriptionName}:{productId}");

            if (ThrowOnCreate is { } exception)
            {
                throw exception;
            }

            if (!_subscriptions.TryGetValue(subscriptionName, out var entry))
            {
                entry = new Entry(displayName, productId, NewKey(), NewKey());
                _subscriptions[subscriptionName] = entry;
            }
            else
            {
                entry.DisplayName = displayName;
                entry.ProductId = productId;
            }

            return Task.FromResult(new ApimSubscriptionWithKeys(Map(subscriptionName, entry), new ApimSubscriptionKeys(entry.PrimaryKey, entry.SecondaryKey)));
        }
    }

    /// <inheritdoc />
    public Task<ApimSubscription?> GetSubscriptionAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"Get:{subscriptionName}");
            return Task.FromResult(_subscriptions.TryGetValue(subscriptionName, out var entry) ? Map(subscriptionName, entry) : null);
        }
    }

    /// <inheritdoc />
    public Task<ApimSubscriptionKeys> ListSecretsAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"ListSecrets:{subscriptionName}");
            var entry = Require(subscriptionName);
            return Task.FromResult(new ApimSubscriptionKeys(entry.PrimaryKey, entry.SecondaryKey));
        }
    }

    /// <inheritdoc />
    public Task RegeneratePrimaryKeyAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"RegeneratePrimary:{subscriptionName}");
            Require(subscriptionName).PrimaryKey = NewKey();
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task RegenerateSecondaryKeyAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"RegenerateSecondary:{subscriptionName}");
            Require(subscriptionName).SecondaryKey = NewKey();
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task UpdateScopeAsync(string subscriptionName, string productId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"UpdateScope:{subscriptionName}:{productId}");
            Require(subscriptionName).ProductId = productId;
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteSubscriptionAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"Delete:{subscriptionName}");
            return Task.FromResult(_subscriptions.Remove(subscriptionName));
        }
    }

    private Entry Require(string subscriptionName) =>
        _subscriptions.TryGetValue(subscriptionName, out var entry)
            ? entry
            : throw new ApimSubscriptionNotFoundException(subscriptionName);

    private static ApimSubscription Map(string subscriptionName, Entry entry) =>
        new(
            subscriptionName,
            $"{ServiceId}/subscriptions/{subscriptionName}",
            entry.DisplayName,
            $"{ServiceId}/products/{entry.ProductId}",
            entry.ProductId,
            "active");

    /// <summary>APIM keys are 32 characters; 16 random bytes as lower-case hex has the same shape.</summary>
    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private sealed class Entry(string displayName, string productId, string primaryKey, string secondaryKey)
    {
        public string DisplayName { get; set; } = displayName;

        public string ProductId { get; set; } = productId;

        public string PrimaryKey { get; set; } = primaryKey;

        public string SecondaryKey { get; set; } = secondaryKey;
    }
}
