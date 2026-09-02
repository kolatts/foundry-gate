using FoundryGate.Core.Quota;
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
    /// When set, the call for this user throws instead of recording — the seam a caller reaches
    /// <em>after</em> it has already staged database changes, which is how a test can prove those
    /// changes are discarded rather than committed by whatever saves next.
    /// </summary>
    public int? ThrowFor { get; set; }

    /// <inheritdoc />
    public Task SyncAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (ThrowFor == user.UserId)
        {
            throw new InvalidOperationException($"Gateway refused the tier move for user {user.UserId}.");
        }

        Calls.Add((user.UserId, tierProductId));
        return Task.CompletedTask;
    }
}
