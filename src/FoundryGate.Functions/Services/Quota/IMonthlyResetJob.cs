using FoundryGate.Core.Quota;

namespace FoundryGate.Functions.Services.Quota;

/// <summary>
/// Everything <c>MonthlyQuotaResetFunction</c> does, minus the trigger attribute (#38): decide whether
/// today is the configured reset day (#165), take the cross-replica lock, and run Core's
/// <see cref="IQuotaResetService"/>. Split out so the behaviour is unit-testable without a Functions
/// host — the function itself is four lines.
/// </summary>
public interface IMonthlyResetJob
{
    /// <summary>Runs one tick of the daily timer. Never throws for "not today" or "someone else has it" — those are outcomes, not failures.</summary>
    Task<MonthlyResetOutcome> RunAsync(CancellationToken cancellationToken);
}

/// <summary>What one tick did.</summary>
/// <param name="SkipReasonType">Why the reset did not run; <see cref="MonthlyResetSkipReasonType.None"/> when it did.</param>
/// <param name="ConfiguredDayOfMonth">The <c>SystemConfiguration[ResetDayOfMonth]</c> in force for this tick.</param>
/// <param name="DayOfMonth">Today's UTC day of month.</param>
/// <param name="Reset">The reset's own outcome, or <see langword="null"/> when it was skipped.</param>
public readonly record struct MonthlyResetOutcome(
    MonthlyResetSkipReasonType SkipReasonType,
    int ConfiguredDayOfMonth,
    int DayOfMonth,
    QuotaResetOutcome? Reset)
{
    /// <summary><see langword="true"/> when the reset actually ran.</summary>
    public bool Ran => SkipReasonType == MonthlyResetSkipReasonType.None;
}

/// <summary>Why a daily tick did nothing.</summary>
public enum MonthlyResetSkipReasonType
{
    /// <summary>It did not skip — the reset ran.</summary>
    None = 0,

    /// <summary>
    /// Today is not <c>SystemConfiguration[ResetDayOfMonth]</c>. The timer fires daily and this gate
    /// decides (#165), rather than the cron expression, so an admin changing the key takes effect the
    /// same day instead of at the next deployment.
    /// </summary>
    NotTheConfiguredDay = 1,

    /// <summary>Another replica holds the reset lock and is running (or has just run) it.</summary>
    LockHeldElsewhere = 2,
}
