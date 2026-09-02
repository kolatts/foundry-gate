namespace FoundryGate.Api.Services.Cost;

/// <summary>
/// Prices reconciled token usage from the fork's own <c>RateCard</c> configuration (#177), so the
/// portal can put a number next to a developer's tokens. Azure cannot answer this question: Claude
/// bills as a single aggregate Marketplace CCU meter, which Cost Management cannot break down per
/// deployment, per subscription or per user.
/// </summary>
/// <remarks>
/// <b>Every figure this returns is an estimate</b>, and the callers say so on screen. It is derived
/// from one blended rate applied to one token total, because that is all
/// <c>QuotaAllocation.TokensUsed</c> is — no prompt/completion split, no per-model split, both
/// tracked by #213 — and the total is itself a floor (interrupted streams undercount, #84;
/// cache-token weighting at the gateway is unverified, #88). See <see cref="RateCard"/>.
/// <para>
/// Cost is a reporting figure and nothing else: the gateway enforces tokens, never dollars.
/// </para>
/// </remarks>
public interface ICostEstimator
{
    /// <summary>
    /// The current rate card. Read from <c>SystemConfiguration</c> behind a short shared cache — the
    /// dashboard and the allocations list would otherwise re-read one row several times per request.
    /// A malformed stored value never fails a read: it is logged and treated as unconfigured, because
    /// a dashboard that 500s over a price list is worse than one with no prices on it.
    /// </summary>
    Task<RateCard> GetRateCardAsync(CancellationToken cancellationToken);
}
