using System.Globalization;
using FoundryGate.Core.Quota;
using FoundryGate.Data;
using FoundryGate.Domain.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Functions.Services.Quota;

/// <summary>
/// Default <see cref="IMonthlyResetJob"/>: the day-of-month gate (#165), the blob lease (#38), and one
/// call into Core's <see cref="IQuotaResetService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a daily timer and a gate, rather than a monthly cron.</b> <c>SystemConfiguration
/// [ResetDayOfMonth]</c> is admin-editable and was, until now, read by nothing at all (#165) —
/// changing it did nothing, which is the exact failure mode the config editor's other guards exist to
/// prevent. A cron expression is fixed at deployment, so honouring the key means waking every day and
/// deciding here. That costs one <c>SystemConfiguration</c> read per day on the days it does nothing,
/// and it is safe because the reset is idempotent: even a bug in this gate can only cause an extra
/// re-resolution, never a lost <c>TokensUsed</c>.
/// </para>
/// <para>
/// <b>What it never does.</b> No APIM call. The gateway's <c>llm-token-limit</c> monthly window is a
/// UTC-truncated calendar month that resets itself (#10 direction update), so there is no counter to
/// clear; enforcement does not depend on this job having run.
/// </para>
/// </remarks>
public sealed class MonthlyResetJob(
    AppDbContext dbContext,
    IQuotaResetService quotaReset,
    IResetLock resetLock,
    TimeProvider timeProvider,
    ILogger<MonthlyResetJob> logger) : IMonthlyResetJob
{
    /// <summary>Name of the lock this job takes; also the lock blob's name.</summary>
    public const string LockName = "quota-monthly-reset";

    /// <summary>Used when <c>SystemConfiguration[ResetDayOfMonth]</c> is missing or unreadable — the seeded value, and spec §6's "always 1 for v1".</summary>
    public const int DefaultResetDayOfMonth = 1;

    /// <summary>The largest day every month has. <c>PUT /config</c> enforces this too (#161), so a value outside it can only come from a hand-edited row.</summary>
    private const int MaxResetDayOfMonth = 28;

    /// <inheritdoc />
    public async Task<MonthlyResetOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var today = timeProvider.GetUtcNow().UtcDateTime.Day;
        var configuredDay = await ReadResetDayOfMonthAsync(cancellationToken);

        if (today != configuredDay)
        {
            logger.LogDebug(
                "Not the configured reset day (today is the {DayOfMonth}, {ConfigKey} is {ConfiguredDayOfMonth}); nothing to do.",
                today,
                SystemConfigurationKeys.ResetDayOfMonth,
                configuredDay);

            return new MonthlyResetOutcome(MonthlyResetSkipReasonType.NotTheConfiguredDay, configuredDay, today, null);
        }

        await using var handle = await resetLock.TryAcquireAsync(LockName, cancellationToken);
        if (!handle.IsAcquired)
        {
            return new MonthlyResetOutcome(MonthlyResetSkipReasonType.LockHeldElsewhere, configuredDay, today, null);
        }

        var outcome = await quotaReset.ResetAsync(QuotaResetTrigger.Scheduled(), cancellationToken);

        logger.LogInformation(
            "Monthly quota reset for {Period}: {UsersResetCount} active users. The gateway's own monthly window resets itself, so no APIM state was touched.",
            outcome.Period,
            outcome.UsersResetCount);

        return new MonthlyResetOutcome(MonthlyResetSkipReasonType.None, configuredDay, today, outcome);
    }

    /// <summary>
    /// <c>SystemConfiguration[ResetDayOfMonth]</c>, or <see cref="DefaultResetDayOfMonth"/> with a
    /// Warning when the row is missing or holds something a calendar cannot honour. Never throws: a
    /// bad configuration value must not be able to stop every future reset.
    /// </summary>
    private async Task<int> ReadResetDayOfMonthAsync(CancellationToken cancellationToken)
    {
        var raw = await dbContext.SystemConfigurations.AsNoTracking()
            .Where(c => c.Key == SystemConfigurationKeys.ResetDayOfMonth)
            .Select(c => c.Value)
            .SingleOrDefaultAsync(cancellationToken);

        if (raw is null)
        {
            logger.LogWarning(
                "SystemConfiguration row '{ConfigKey}' is missing; resetting on day {DefaultResetDayOfMonth}. Run the reference-data seed (`foundrygate db seed-reference`).",
                SystemConfigurationKeys.ResetDayOfMonth,
                DefaultResetDayOfMonth);

            return DefaultResetDayOfMonth;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)
            || day < 1
            || day > MaxResetDayOfMonth)
        {
            logger.LogWarning(
                "SystemConfiguration['{ConfigKey}'] = '{ConfigValue}' is not a day from 1 to {MaxResetDayOfMonth}; resetting on day {DefaultResetDayOfMonth} instead. Fix the value on the admin /config page.",
                SystemConfigurationKeys.ResetDayOfMonth,
                raw,
                MaxResetDayOfMonth,
                DefaultResetDayOfMonth);

            return DefaultResetDayOfMonth;
        }

        return day;
    }
}
