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
/// The APIM tier product (<see cref="Constants.GatewayTiers"/>) the numeric quota mapped to — the
/// smallest tier whose cap covers it. <b>This is what the gateway enforces</b>, not
/// <paramref name="AllocatedTokens"/>: APIM's <c>token-quota</c> is a per-product literal, so a
/// developer on the Standard tier is cut off at that tier's cap, whatever their numeric quota says.
/// </param>
/// <param name="IsGatewayCapped">
/// True when <paramref name="AllocatedTokens"/> exceeds every finite tier's cap, so the developer
/// landed on the largest finite tier and the gateway will 403 at <em>that tier's</em> cap, below
/// their numeric quota. Surfaced so admins can see the allocation is not fully honoured and either
/// raise the tier caps (infra) or grant unlimited.
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
/// Result of POST /quota/reset (spec &#167;6, admin-triggered manual reset). Idempotent: every
/// active user's allocation for the current UTC calendar month is (re)resolved; rows that already
/// exist keep their reconciled <c>TokensUsed</c> (the gateway's monthly window resets itself — issue
/// #10 direction update — so zeroing the mirror mid-month would just make the dashboard lie).
/// </summary>
/// <param name="UsersResetCount">Active users whose allocation was created or re-resolved.</param>
/// <param name="PeriodYear">Calendar year (UTC) of the period that was reset.</param>
/// <param name="PeriodMonth">Calendar month (UTC, 1-12) of the period that was reset.</param>
/// <param name="ResetDate">When the reset ran — also written to every touched row's <c>ResetDate</c>.</param>
public record QuotaResetResult(int UsersResetCount, int PeriodYear, int PeriodMonth, DateTimeOffset ResetDate);
