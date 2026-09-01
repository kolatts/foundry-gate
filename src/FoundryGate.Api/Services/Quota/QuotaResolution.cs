using FoundryGate.Data.Entities;
using FoundryGate.Domain.Quota;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// What one <see cref="IQuotaResolutionService.ResolveAsync"/> call did: the upserted (tracked, not
/// yet saved) allocation plus enough about the transition for callers to react without re-querying.
/// </summary>
/// <param name="Allocation">The tracked <see cref="QuotaAllocation"/> for the (user, period) — Added when <paramref name="IsNew"/>, Modified (or Unchanged) otherwise. Level, tier and capped flag are on it.</param>
/// <param name="IsNew">True when no row existed for the period and one was added with <c>TokensUsed = 0</c>.</param>
/// <param name="PreviousTierProductId">The tier on the same-period row before this call, else on the user's most recent earlier allocation; <see langword="null"/> when no earlier allocation is known.</param>
/// <param name="TierSyncRequested">True when <see cref="IGatewayTierSync.SyncAsync"/> was invoked for this resolution (user has an APIM subscription and the tier changed or was unknown).</param>
public sealed record QuotaResolution(
    QuotaAllocation Allocation,
    bool IsNew,
    string? PreviousTierProductId,
    bool TierSyncRequested)
{
    /// <summary>Shortcut for <c>Allocation.ResolvedLevelType</c>.</summary>
    public QuotaLevelType LevelType => Allocation.ResolvedLevelType;

    /// <summary>Shortcut for <c>Allocation.AllocatedTokens</c> (<see langword="null"/> = unlimited).</summary>
    public long? AllocatedTokens => Allocation.AllocatedTokens;

    /// <summary>Shortcut for <c>Allocation.TierProductId</c>.</summary>
    public string TierProductId => Allocation.TierProductId;

    /// <summary>Shortcut for <c>Allocation.IsGatewayCapped</c>.</summary>
    public bool IsGatewayCapped => Allocation.IsGatewayCapped;

    /// <summary>True when the tier differs from <see cref="PreviousTierProductId"/> (an unknown previous tier counts as a change).</summary>
    public bool TierChanged => !string.Equals(PreviousTierProductId, Allocation.TierProductId, StringComparison.Ordinal);
}
