namespace FoundryGate.Domain.Quota;

/// <summary>
/// Which level of the five-level precedence chain (spec &#167;3.2; issue #32) produced a
/// <c>QuotaAllocation</c>. Recorded on the allocation so the UI can explain to a developer
/// <em>why</em> they have the quota they have. Stored as <c>int</c> (CONVENTIONS.md: "Enums stored
/// as int, property suffixed Type"). Declaration order is precedence order — user-level settings
/// (<see cref="UserUnlimited"/>, <see cref="UserOverride"/>) always beat group-level ones
/// (<see cref="GroupUnlimited"/>, <see cref="GroupMax"/>), which beat the system default.
/// </summary>
public enum QuotaLevelType
{
    /// <summary>Level 1: <c>User.IsUnlimited</c> — unlimited, regardless of any group.</summary>
    UserUnlimited = 0,

    /// <summary>Level 2: <c>User.MonthlyTokenQuota</c> is set — that number, regardless of any group.</summary>
    UserOverride = 1,

    /// <summary>Level 3: at least one of the user's groups has <c>Group.IsUnlimited</c>.</summary>
    GroupUnlimited = 2,

    /// <summary>Level 4: the largest <c>Group.MonthlyTokenQuota</c> across the user's groups.</summary>
    GroupMax = 3,

    /// <summary>Level 5: <c>SystemConfiguration[DefaultMonthlyTokenQuota]</c>.</summary>
    SystemDefault = 4,
}
