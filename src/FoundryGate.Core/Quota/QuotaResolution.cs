using FoundryGate.Data.Entities;
using FoundryGate.Domain.Quota;

namespace FoundryGate.Core.Quota;

/// <summary>
/// What one <see cref="IQuotaResolutionService.ResolveAsync"/> call did: the upserted (tracked, not
/// yet saved) allocation plus enough about the transition for callers to react without re-querying.
/// Level, tier and the capped flag are read off <see cref="Allocation"/>.
/// </summary>
/// <param name="Allocation">The tracked <see cref="QuotaAllocation"/> for the (user, period) — Added when <paramref name="IsNew"/>, Modified (or Unchanged) otherwise.</param>
/// <param name="IsNew">True when no row existed for the period and one was added with <c>TokensUsed = 0</c>.</param>
/// <param name="PreviousTierProductId">The tier on the same-period row before this call, else on the user's most recent earlier allocation; <see langword="null"/> when no earlier allocation is known.</param>
/// <param name="TierSyncRequired">
/// True when this user's gateway subscription needs moving: they have a non-empty
/// <see cref="QuotaAllocation.UserId"/>-owned <c>ApimSubscriptionId</c> and the resolved tier differs
/// from <paramref name="PreviousTierProductId"/> (or none was known). Says a move is <em>needed</em>,
/// not that it happened.
/// </param>
/// <param name="TierSyncRequested">
/// True when <see cref="IGatewayTierSync.SyncAsync"/> was actually invoked during this call — i.e.
/// <b>this unit of work reached APIM</b>, which is the predicate <c>CommitToken.For</c> wants. Equal to
/// <paramref name="TierSyncRequired"/> under <see cref="GatewayTierSyncMode.Immediate"/>; always false
/// under <see cref="GatewayTierSyncMode.Deferred"/>, where the caller drives the moves itself and owns
/// the commit point for each one.
/// </param>
public sealed record QuotaResolution(
    QuotaAllocation Allocation,
    bool IsNew,
    string? PreviousTierProductId,
    bool TierSyncRequired,
    bool TierSyncRequested);

/// <summary>
/// Who calls <see cref="IGatewayTierSync"/> for the moves a
/// <see cref="IQuotaResolutionService.ResolveManyAsync"/> pass turns out to need.
/// </summary>
/// <remarks>
/// A batch that moves subscriptions <em>inside</em> its own loop and saves once at the end cannot be
/// all-or-nothing: by the time user <c>N</c>'s move fails, users <c>1..N-1</c> are already re-scoped at
/// the gateway, and the rollback discards the rows describing them (#211 review). Request-time callers
/// (a group edit, an Entra sync) resolve a handful of users and genuinely want the whole thing to fail
/// together, so <see cref="Immediate"/> stays their default; the monthly reset spans every active
/// developer and takes <see cref="Deferred"/> so it can commit each move as it happens.
/// </remarks>
public enum GatewayTierSyncMode
{
    /// <summary>Move each subscription as part of resolving it. One failure aborts the whole pass, and nothing is saved.</summary>
    Immediate = 0,

    /// <summary>
    /// Touch no gateway at all: report the needed moves on <see cref="QuotaResolution.TierSyncRequired"/>
    /// and leave the caller to perform and commit them one at a time.
    /// </summary>
    Deferred = 1,
}

/// <summary>
/// What the five-level chain <em>says</em> a user's budget is, with nothing written down: no
/// <see cref="QuotaAllocation"/> upsert, no <see cref="IGatewayTierSync"/> call, nothing added to the
/// change tracker. The answer to "what is this developer's quota right now?" for callers that need it
/// to decide whether to act at all — see <see cref="IQuotaResolutionService.PreviewAsync"/>.
/// </summary>
/// <param name="Level">The level that produced <paramref name="Quota"/>.</param>
/// <param name="Quota">The resolved monthly token quota; <see langword="null"/> means unlimited.</param>
public readonly record struct QuotaPreview(QuotaLevelType Level, long? Quota);
