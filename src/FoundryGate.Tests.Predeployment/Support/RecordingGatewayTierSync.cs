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

    /// <inheritdoc />
    public Task SyncAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        Calls.Add((user.UserId, tierProductId));
        return Task.CompletedTask;
    }
}
