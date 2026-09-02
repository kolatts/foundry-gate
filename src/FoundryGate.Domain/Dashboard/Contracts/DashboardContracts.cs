namespace FoundryGate.Domain.Dashboard.Contracts;

/// <summary>Admin dashboard summary stats (spec &#167;4.6: "total users, active, top consumers"). GET /dashboard.</summary>
/// <remarks>
/// <see cref="TotalTokensUsedThisPeriod"/>, <see cref="OverBudgetUserCount"/> and every consumer's
/// usage figure are reconciliation numbers from the Log Analytics sync (spec &#167;5.4), refreshed on
/// the sync job's cadence — not a live view of gateway enforcement state.
/// <para>
/// The two counts added by #190 are appended rather than inserted: this is a positional record the
/// Web client deserializes, and re-ordering its parameters is a silent wire break.
/// </para>
/// </remarks>
/// <param name="TotalUserCount">Every <c>User</c> row, active or not.</param>
/// <param name="ActiveUserCount">Users whose account is active.</param>
/// <param name="UnlimitedUserCount">Active users with no monthly cap.</param>
/// <param name="PendingQuotaIncreaseRequestCount">Quota increase requests still waiting for a reviewer.</param>
/// <param name="TotalTokensUsedThisPeriod">Reconciled tokens across every allocation in the current period.</param>
/// <param name="TopConsumers">The busiest active users this period, most tokens first.</param>
/// <param name="HardStoppedUserCount">
/// Active users whose current-period allocation carries <c>IsHardStopped</c>. That flag is set by the
/// deprovision pipeline (which also deactivates the account) and cleared by re-activation and by the
/// monthly reset; quota exhaustion never sets it, because the gateway enforces the monthly quota
/// itself with a 403. An <em>active</em> user carrying it is therefore an inconsistency worth
/// surfacing — the allocation says "stopped" while the account says "live" — and is normally zero.
/// For "who has run out of tokens", read <see cref="OverBudgetUserCount"/>.
/// </param>
/// <param name="OverBudgetUserCount">
/// Active users whose current-period allocation has a finite budget that reconciled usage has reached
/// or passed (<c>TokensUsed &gt;= AllocatedTokens</c>). Enforcement is the gateway's
/// <c>token-quota</c> policy, so these developers are already being refused; this count is how an
/// admin finds them before they ask. Unlimited allocations are never counted.
/// </param>
/// <param name="EstimatedCostThisPeriod">
/// <see cref="TotalTokensUsedThisPeriod"/> priced at the fork's configured <c>RateCard</c> blended
/// rate (#177), or <see langword="null"/> when no rate card is configured — which is how a fork
/// ships, and a truer answer than a zero that reads as "free".
/// <para>
/// <b>An estimate, and labelled one everywhere it is rendered.</b> One blended rate is applied to
/// one token total because that is all the control plane stores: <c>QuotaAllocation.TokensUsed</c>
/// has no prompt/completion split and no per-model split (#213 stores both). The total is itself a
/// floor (interrupted streams undercount, #84; cache-token weighting at the gateway is unverified,
/// #88). Cost is a reporting figure — the gateway enforces tokens, never dollars.
/// </para>
/// </param>
public record DashboardSummaryResponse(
    int TotalUserCount,
    int ActiveUserCount,
    int UnlimitedUserCount,
    int PendingQuotaIncreaseRequestCount,
    long TotalTokensUsedThisPeriod,
    IReadOnlyList<TopConsumerResponse> TopConsumers,
    int HardStoppedUserCount,
    int OverBudgetUserCount,
    decimal? EstimatedCostThisPeriod);

/// <summary>One entry in the dashboard's top-consumers list.</summary>
/// <remarks>Like its parent, this is a positional record the Web client deserializes: append, never insert.</remarks>
/// <param name="UserId">The consumer's int PK.</param>
/// <param name="UserUnique">The consumer's externally-shared stable id.</param>
/// <param name="DisplayName">The consumer's display name.</param>
/// <param name="TokensUsed">Reconciled tokens this period.</param>
/// <param name="AllocatedTokens">Their budget; null when unlimited.</param>
/// <param name="PercentUsed">Null when unlimited; otherwise the share of the budget spent.</param>
/// <param name="EstimatedCostThisPeriod">This consumer's <paramref name="TokensUsed"/> at the blended rate, or null when no rate card is configured. An estimate — see <see cref="DashboardSummaryResponse.EstimatedCostThisPeriod"/>.</param>
public record TopConsumerResponse(
    int UserId,
    Guid UserUnique,
    string DisplayName,
    long TokensUsed,
    long? AllocatedTokens,
    double? PercentUsed,
    decimal? EstimatedCostThisPeriod);
