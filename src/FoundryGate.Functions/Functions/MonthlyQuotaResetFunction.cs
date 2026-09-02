using FoundryGate.Functions.Services.Quota;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Functions.Functions;

/// <summary>
/// The scheduled monthly quota reset (#38): re-resolve every active developer's allocation for the
/// new period, clear the hard-stop mirror, write one audit row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Daily at 00:01 UTC, not monthly.</b> The cron is <c>0 1 0 * * *</c> and
/// <see cref="IMonthlyResetJob"/> decides whether today is
/// <c>SystemConfiguration[ResetDayOfMonth]</c> (#165) — an admin-editable key that, before this,
/// nothing read. A monthly cron would fix the day at deployment time and quietly ignore the setting.
/// </para>
/// <para>
/// <b><c>RunOnStartup</c> is off</b> and stays off: every deployment restarts the worker, and a reset
/// that fired on each restart would stamp <c>ResetDate</c> across every allocation for no reason.
/// </para>
/// </remarks>
public class MonthlyQuotaResetFunction(IMonthlyResetJob job, ILogger<MonthlyQuotaResetFunction> logger)
{
    /// <summary>Runs one daily tick. Exceptions propagate so the Functions host records a failed invocation and retries on the next tick.</summary>
    [Function(nameof(MonthlyQuotaResetFunction))]
    public async Task RunAsync([TimerTrigger("0 1 0 * * *", RunOnStartup = false)] TimerInfo timer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);

        var outcome = await job.RunAsync(cancellationToken);

        logger.LogInformation(
            "Monthly reset tick: ran={Ran}, reason={SkipReasonType}, day={DayOfMonth}, configuredDay={ConfiguredDayOfMonth}. Next schedule {NextSchedule:u}.",
            outcome.Ran,
            outcome.SkipReasonType,
            outcome.DayOfMonth,
            outcome.ConfiguredDayOfMonth,
            timer.ScheduleStatus?.Next);
    }
}
