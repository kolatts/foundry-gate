namespace FoundryGate.Domain.Dashboard.Contracts;

/// <summary>Admin dashboard summary stats (spec &#167;4.6: "total users, active, top consumers"). GET /dashboard.</summary>
/// <remarks>
/// <see cref="TotalTokensUsedThisPeriod"/> and every consumer's usage figure are
/// reconciliation numbers from the Log Analytics sync (spec &#167;5.4), refreshed on the
/// sync job's cadence — not a live view of gateway enforcement state.
/// </remarks>
public record DashboardSummaryResponse(
    int TotalUserCount,
    int ActiveUserCount,
    int UnlimitedUserCount,
    int PendingQuotaIncreaseRequestCount,
    long TotalTokensUsedThisPeriod,
    IReadOnlyList<TopConsumerResponse> TopConsumers);

/// <summary>One entry in the dashboard's top-consumers list.</summary>
public record TopConsumerResponse(
    int UserId,
    Guid UserUnique,
    string DisplayName,
    long TokensUsed,
    long? AllocatedTokens,
    double? PercentUsed);
