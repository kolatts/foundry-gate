using FoundryGate.Data.Entities;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Core.Quota;

/// <summary>
/// The <see cref="IGatewayTierSync"/> for a host that has a gateway but cannot reach its management
/// plane: it records at <b>Warning</b> that a developer's tier moved in the database while their APIM
/// subscription did not, and moves on. Registered by the Functions host, which runs quota resolution
/// (the monthly reset) but does not carry the APIM key service <c>ApimGatewayTierSync</c> composes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than <see cref="NullGatewayTierSync"/>.</b> The original claim — "a reset
/// re-resolves inputs nobody changed, so it cannot produce a tier change" — holds only for *per-user*
/// inputs. It is false for the system default: <c>PUT /config</c> on
/// <c>SystemConfiguration[DefaultMonthlyTokenQuota]</c> re-resolves nobody, so the next scheduled reset
/// is the first thing to notice, and it would move every default-tier user's <c>TierProductId</c> and
/// <c>AllocatedTokens</c> in SQL while the gateway kept enforcing the old product. The dashboard would
/// say 20M and the gateway would 403 at 5M, with nothing above Debug saying so. It is also false for a
/// user with no earlier allocation, whose previous tier is unknown and therefore always "changed".
/// </para>
/// <para>
/// <b>This is a stopgap, and the Warning says so.</b> Two issues close the hole properly:
/// <c>PUT /config</c> re-resolving affected users in the Api, where <c>ApimGatewayTierSync</c> lives
/// (#193), and moving the APIM management client into Core so this host can do the move itself (#194).
/// Until one of them lands, the database and the gateway reconverge on the user's next quota touch
/// from the Api — a quota change, a group edit, an approval, or their next
/// <c>GET /quota/allocations/me</c> of a period with no row — because resolution compares against the
/// tier recorded on the allocation and re-fires the sync there.
/// </para>
/// <para>
/// It never throws: failing the monthly reset for every developer because one tier is out of step
/// would be a worse outcome than the drift it is reporting. The reset counts these calls into its
/// audit row (<c>tierChangeCount</c>) so the trail records how many users are affected, not just the
/// log.
/// </para>
/// </remarks>
public sealed class WarningGatewayTierSync(ILogger<WarningGatewayTierSync> logger) : IGatewayTierSync
{
    /// <inheritdoc />
    public Task SyncAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(tierProductId);

        logger.LogWarning(
            "Quota tier changed to {TierProductId} for user {UserId}, but this host cannot move the APIM subscription {ApimSubscriptionId} — it has no APIM management client (#194). The gateway keeps enforcing the previous product until the API reconciles on the user's next quota touch. If this appears for many users at once, DefaultMonthlyTokenQuota was changed without re-resolving them (#193).",
            tierProductId,
            user.UserId,
            user.ApimSubscriptionId);

        return Task.CompletedTask;
    }
}
