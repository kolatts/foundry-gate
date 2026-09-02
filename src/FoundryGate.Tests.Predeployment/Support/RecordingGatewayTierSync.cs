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

    /// <summary>The same calls with the previous tier the caller passed (#211 review) — what tells a genuine no-op from an unrecorded move.</summary>
    public List<(int UserId, string TierProductId, string? PreviousTierProductId)> Records { get; } = [];

    /// <summary>
    /// When set, the call for this user throws instead of recording — the seam a caller reaches
    /// <em>after</em> it has already staged database changes, which is how a test can prove those
    /// changes are discarded rather than committed by whatever saves next.
    /// </summary>
    public int? ThrowFor { get; set; }

    /// <summary>
    /// Runs immediately after a sync is recorded and before the caller gets control back — the seam for
    /// the "client disconnects the instant the gateway accepted the tier move" probe (#163), mirroring
    /// <see cref="FakeApimManagementClient.AfterMutation"/>.
    /// </summary>
    public Action? AfterSync { get; set; }

    /// <inheritdoc />
    public Task SyncAsync(User user, string tierProductId, string? previousTierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (ThrowFor == user.UserId)
        {
            throw new InvalidOperationException($"Gateway refused the tier move for user {user.UserId}.");
        }

        Calls.Add((user.UserId, tierProductId));
        Records.Add((user.UserId, tierProductId, previousTierProductId));
        AfterSync?.Invoke();
        return Task.CompletedTask;
    }
}
