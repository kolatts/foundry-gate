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
/// <param name="ResetDate">When this period's allocation row was last (re)computed, e.g. by the monthly reset job.</param>
public record QuotaAllocationResponse(
    int QuotaAllocationId,
    int UserId,
    Guid UserUnique,
    int PeriodYear,
    int PeriodMonth,
    bool IsUnlimited,
    long? AllocatedTokens,
    long TokensUsed,
    double? PercentUsed,
    bool IsHardStopped,
    DateTimeOffset? ResetDate);

/// <summary>Result of POST /quota/reset (spec &#167;6, admin-triggered manual reset).</summary>
public record QuotaResetResult(int UsersResetCount, DateTimeOffset ResetDate);
