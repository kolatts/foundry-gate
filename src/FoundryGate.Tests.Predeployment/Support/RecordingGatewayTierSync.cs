using FoundryGate.Api.Services.Quota;
using FoundryGate.Data.Entities;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// Hand-rolled <see cref="IGatewayTierSync"/> that records every call (CONVENTIONS.md: no mocking
/// library), so resolution tests can assert the seam is invoked exactly when the tier changes.
/// </summary>
public sealed class RecordingGatewayTierSync : IGatewayTierSync
{
    public List<(int UserId, string TierProductId)> Calls { get; } = [];

    /// <summary>
    /// Runs immediately after a sync is recorded and before the caller gets control back — the seam for
    /// the "client disconnects the instant the gateway accepted the tier move" probe (#163), mirroring
    /// <see cref="FakeApimManagementClient.AfterMutation"/>.
    /// </summary>
    public Action? AfterSync { get; set; }

    /// <inheritdoc />
    public Task SyncAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        Calls.Add((user.UserId, tierProductId));
        AfterSync?.Invoke();
        return Task.CompletedTask;
    }
}
