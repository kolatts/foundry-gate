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
    /// <param name="fresh">
    /// Bypasses the cache and re-reads the row. <c>GET /dashboard?fresh=true</c> passes its own flag
    /// through: an admin who has just corrected a price and hit Refresh must not be served the price
    /// they came to replace, and a cache nested inside a cache would otherwise make the documented
    /// escape hatch a lie.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RateCard> GetRateCardAsync(bool fresh, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the cached card. Called by <c>PUT /config/RateCard</c> once the write has committed, so
    /// a corrected price is live on the next read rather than up to <see cref="CostEstimator.CacheDuration"/>
    /// later — and never up to that plus the dashboard's own cache window.
    /// </summary>
    void Invalidate();
}
