using System.Security.Cryptography;
using Azure;
using FoundryGate.Core.Gateway;

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

    /// <summary>When set, <see cref="ListSecretsAsync"/> throws it — simulates ARM failing between "keys regenerated" and "new key read".</summary>
    public Exception? ThrowOnListSecrets { get; set; }

    /// <summary>
    /// When set, <see cref="RegeneratePrimaryKeyAsync"/> throws it — simulates ARM failing, or the
    /// caller disconnecting, while the one call that kills the developer's live key is in flight.
    /// </summary>
    public Exception? ThrowOnRegeneratePrimaryKey { get; set; }

    /// <summary>
    /// When set, <see cref="RegenerateSecondaryKeyAsync"/> throws it — the never-issued key failing to
    /// rotate, which must not cost the developer the primary they were just handed.
    /// </summary>
    public Exception? ThrowOnRegenerateSecondaryKey { get; set; }

    /// <summary>When set, <see cref="UpdateScopeAsync"/> throws it instead of re-scoping — simulates ARM refusing a tier move (a missing role, a 429, a 5xx).</summary>
    public Exception? ThrowOnUpdateScope { get; set; }

    /// <summary>When set, <see cref="DeleteSubscriptionAsync"/> throws it instead of deleting — simulates ARM refusing a deprovision.</summary>
    public Exception? ThrowOnDelete { get; set; }

    /// <summary>
    /// Subscription names whose <see cref="DeleteSubscriptionAsync"/> throws a <c>429</c> — for asserting
    /// that one failed deprovision in a batch does not take the successful ones down with it.
    /// </summary>
    public HashSet<string> FailDeleteFor { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs immediately after APIM has accepted a mutation — create, re-scope, either key regeneration,
    /// or delete — and before the caller gets control back. The hook for the "client disconnects the
    /// instant the external system said yes" probe: everything the caller does after this point must
    /// survive a cancelled token (CONVENTIONS.md commit point).
    /// </summary>
    public Action? AfterMutation { get; set; }

    /// <summary>Changes a subscription's state behind the key service's back (e.g. <c>"suspended"</c> for a hand-made orphan).</summary>
    public void SetState(string subscriptionName, string state)
    {
        lock (_gate)
        {
            _subscriptions[subscriptionName].State = state;
        }
    }

    /// <inheritdoc />
    public string GetSubscriptionResourceId(string subscriptionName) => $"{ServiceId}/subscriptions/{subscriptionName}";

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

    /// <summary>Moves a subscription to another product behind the caller's back — simulates a move whose database record never committed.</summary>
    public void SetProduct(string subscriptionName, string productId)
    {
        lock (_gate)
        {
            _subscriptions[subscriptionName].ProductId = productId;
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

            AfterMutation?.Invoke();
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
            if (ThrowOnListSecrets is { } exception)
            {
                throw exception;
            }

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

            if (ThrowOnRegeneratePrimaryKey is { } primaryException)
            {
                throw primaryException;
            }

            Require(subscriptionName).PrimaryKey = NewKey();
            AfterMutation?.Invoke();
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task RegenerateSecondaryKeyAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"RegenerateSecondary:{subscriptionName}");

            if (ThrowOnRegenerateSecondaryKey is { } secondaryException)
            {
                throw secondaryException;
            }

            Require(subscriptionName).SecondaryKey = NewKey();
            AfterMutation?.Invoke();
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task UpdateScopeAsync(string subscriptionName, string productId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"UpdateScope:{subscriptionName}:{productId}");

            if (ThrowOnUpdateScope is { } exception)
            {
                throw exception;
            }

            Require(subscriptionName).ProductId = productId;
            AfterMutation?.Invoke();
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteSubscriptionAsync(string subscriptionName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add($"Delete:{subscriptionName}");

            if (ThrowOnDelete is { } exception)
            {
                throw exception;
            }

            if (FailDeleteFor.Contains(subscriptionName))
            {
                throw new RequestFailedException(429, $"Too many requests deleting {subscriptionName}.");
            }

            var removed = _subscriptions.Remove(subscriptionName);
            AfterMutation?.Invoke();
            return Task.FromResult(removed);
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
            entry.State);

    /// <summary>APIM keys are 32 characters; 16 random bytes as lower-case hex has the same shape.</summary>
    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private sealed class Entry(string displayName, string productId, string primaryKey, string secondaryKey)
    {
        public string DisplayName { get; set; } = displayName;

        public string ProductId { get; set; } = productId;

        public string PrimaryKey { get; set; } = primaryKey;

        public string SecondaryKey { get; set; } = secondaryKey;

        public string State { get; set; } = "active";
    }
}
