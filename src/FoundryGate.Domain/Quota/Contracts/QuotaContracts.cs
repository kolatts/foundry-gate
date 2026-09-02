namespace FoundryGate.Domain.Quota.Contracts;

/// <summary>
/// A user's resolved token allocation for one billing period (spec &#167;3.1
/// <c>QuotaAllocation</c>, &#167;3.2 resolution logic). Returned by GET
/// /quota/allocations (paged), /quota/allocations/me, /quota/allocations/{userId}, and
/// embedded in <see cref="Users.Contracts.UserProfileResponse"/> for <c>/me</c>'s quota
/// gauge.
/// </summary>
/// <remarks>
/// <see cref="AllocatedTokens"/>, <see cref="TokensUsed"/>, and <see cref="PercentUsed"/>
/// are reconciliation numbers synced from the <c>ApiManagementGatewayLlmLog</c> Log
/// Analytics table (spec &#167;5.4) — a dashboard/audit view, not the enforcement
/// boundary. Quota is enforced in real time at the APIM gateway (<c>llm-token-limit</c>:
/// 429 on the per-minute cap, 403 on the monthly cap); the gateway's own response
/// headers (<c>x-fg-remaining-quota</c>, <c>x-fg-remaining-tpm</c>) are the live source
/// of truth for whether a given request will succeed, not this DTO.
/// </remarks>
/// <param name="QuotaAllocationId">Surrogate int PK.</param>
/// <param name="UserId">Owning user's int PK.</param>
/// <param name="UserUnique">Owning user's externally-shared stable id.</param>
/// <param name="UserDisplayName">Owning user's display name, denormalized for the admin list (issue #33: "with user info").</param>
/// <param name="UserEmail">Owning user's email, denormalized for the admin list.</param>
/// <param name="PeriodYear">Calendar year of the billing period.</param>
/// <param name="PeriodMonth">Calendar month (1-12) of the billing period.</param>
/// <param name="IsUnlimited">Derived: true when <paramref name="AllocatedTokens"/> is null.</param>
/// <param name="AllocatedTokens">Null means unlimited for this period.</param>
/// <param name="TokensUsed">Reconciled token usage for the period so far.</param>
/// <param name="PercentUsed">Null when unlimited; otherwise <c>TokensUsed / AllocatedTokens * 100</c>, computed by the API.</param>
/// <param name="IsHardStopped">
/// Set when an admin revokes this user's key (spec &#167;5.3), not automatically on
/// quota exhaustion — quota exhaustion alone never sets this; the gateway just starts
/// returning 403 until the next monthly reset.
/// </param>
/// <param name="ResolvedLevelType">Which level of the five-level precedence chain produced <paramref name="AllocatedTokens"/> — so the UI can say "from your group Platform Engineering" rather than just a number.</param>
/// <param name="TierProductId">
/// The APIM tier product (<see cref="Constants.GatewayTiers"/>) this budget is. A monthly budget
/// <em>is</em> a tier: every quota the control plane accepts equals a configured tier cap or is
/// unlimited (see <see cref="QuotaTierResponse"/>), so normally <paramref name="AllocatedTokens"/> and
/// this tier's cap are the same number. <b>The tier is what the gateway enforces</b> — APIM's
/// <c>token-quota</c> is a per-product literal.
/// </param>
/// <param name="IsGatewayCapped">
/// True when <paramref name="AllocatedTokens"/> did not match any configured tier cap (a legacy or
/// hand-edited value) and is therefore enforced at the next tier up — or the largest finite tier —
/// rather than at the number shown. Surfaced so admins can correct the value to a tier.
/// </param>
/// <param name="ResetDate">When this period's allocation row was last (re)computed by a monthly/manual reset; null for a row created on demand (first <c>/me</c> of the month).</param>
public record QuotaAllocationResponse(
    int QuotaAllocationId,
    int UserId,
    Guid UserUnique,
    string UserDisplayName,
    string UserEmail,
    int PeriodYear,
    int PeriodMonth,
    bool IsUnlimited,
    long? AllocatedTokens,
    long TokensUsed,
    double? PercentUsed,
    bool IsHardStopped,
    QuotaLevelType ResolvedLevelType,
    string TierProductId,
    bool IsGatewayCapped,
    DateTimeOffset? ResetDate);

/// <summary>
/// One configured budget tier — GET /quota/tiers (any authenticated user). The values an admin (or
/// an approval) may set as a monthly token quota are exactly these: a finite quota must equal a
/// tier's <paramref name="MonthlyTokenQuota"/>, or be unlimited. The UI offers these as the choices
/// rather than a free-form number.
/// </summary>
/// <param name="ProductId">APIM product id (<see cref="Constants.GatewayTiers"/>), e.g. <c>standard</c>.</param>
/// <param name="DisplayName">Human-readable tier name, e.g. <c>Standard</c>.</param>
/// <param name="MonthlyTokenQuota">The tier's cap; <see langword="null"/> for the unlimited tier (same convention as <see cref="QuotaAllocationResponse.AllocatedTokens"/>).</param>
/// <param name="IsUnlimited">True for the tier with no gateway-enforced monthly budget.</param>
public record QuotaTierResponse(
    string ProductId,
    string DisplayName,
    long? MonthlyTokenQuota,
    bool IsUnlimited);

/// <summary>
/// Result of POST /quota/reset (spec &#167;6, admin-triggered manual reset). Idempotent: every
/// active user's allocation for the current UTC calendar month is (re)resolved; rows that already
/// exist keep their reconciled <c>TokensUsed</c> (the gateway's monthly window resets itself — issue
/// #10 direction update — so zeroing the mirror mid-month would just make the dashboard lie).
/// </summary>
/// <param name="UsersResetCount">Active users whose allocation was created or re-resolved.</param>
/// <param name="PeriodYear">Calendar year (UTC) of the period that was reset.</param>
/// <param name="PeriodMonth">Calendar month (UTC, 1-12) of the period that was reset.</param>
/// <param name="ResetDate">When the reset ran — also written to every touched row's <c>ResetDate</c>.</param>
/// <param name="ExpiredRequestCount">
/// Quota increase requests left pending from an earlier period that this run closed as <c>Rejected</c>
/// (#159). Usually zero; reported so an admin who clears six stale requests is told, rather than having
/// to find it in the audit log.
/// </param>
public record QuotaResetResult(
    int UsersResetCount,
    int PeriodYear,
    int PeriodMonth,
    DateTimeOffset ResetDate,
    int ExpiredRequestCount);
