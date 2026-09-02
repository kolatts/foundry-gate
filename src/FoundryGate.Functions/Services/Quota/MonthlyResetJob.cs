using System.Globalization;
using FoundryGate.Core.Quota;
using FoundryGate.Data;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
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
/// <b>The gate is "on or after the configured day, and not yet done this period".</b> An equality gate
/// turns one failed tick — a storage blip, a cold start that timed out, a deployment landing at 00:01 —
/// into a lost month, which contradicted every "the next tick retries" comment in this area. "Already
/// done" is read from the <c>quota.monthly-reset</c> audit row, which commits in the same transaction
/// as the allocations, so there is no separate state to keep honest.
/// </para>
/// <para>
/// <b>What it never does for its own sake.</b> The gateway's <c>llm-token-limit</c> monthly window is
/// a UTC-truncated calendar month that resets itself (#10 direction update), so there is no counter
/// to clear and enforcement does not depend on this job having run — a plain reset makes no APIM
/// call. A tier that <em>does</em> move during a reset (the <c>DefaultMonthlyTokenQuota</c> case)
/// is the exception: since #194 this host re-scopes that developer's subscription for real through
/// <see cref="ApimGatewayTierSync"/>, writing a <c>key.tier-changed</c> row that commits with the
/// run, and the moves are still counted in the run's own audit row (<c>tierChangeCount</c>).
/// A failed move therefore fails the run, which is the correct trade: the alternative is a database
/// that claims a budget the gateway is not enforcing.
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
        var now = timeProvider.GetUtcNow();
        var today = now.UtcDateTime.Day;
        var period = BillingPeriod.FromInstant(now);
        var configuredDay = await ReadResetDayOfMonthAsync(cancellationToken);

        // "On or after the configured day", not "on the configured day": a single failed tick — a
        // storage blip, a cold-start timeout, a deployment landing at 00:01 — must not cost the month
        // its reset. Every comment in this area promises "the next tick retries", and with an equality
        // gate none of them were true.
        if (today < configuredDay)
        {
            logger.LogDebug(
                "Before the configured reset day (today is the {DayOfMonth}, {ConfigKey} is {ConfiguredDayOfMonth}); nothing to do.",
                today,
                SystemConfigurationKeys.ResetDayOfMonth,
                configuredDay);

            return new MonthlyResetOutcome(MonthlyResetSkipReasonType.BeforeTheConfiguredDay, configuredDay, today, null);
        }

        // What stops it running on all 30 remaining days of the month. Idempotence already made a
        // second run harmless, but "harmless" is not "free": it is a full re-resolution of every active
        // user and an audit row nobody asked for.
        if (await AlreadyResetAsync(period, cancellationToken))
        {
            logger.LogDebug("{Period} has already been reset; nothing to do.", period);

            return new MonthlyResetOutcome(MonthlyResetSkipReasonType.AlreadyResetThisPeriod, configuredDay, today, null);
        }

        await using var handle = await resetLock.TryAcquireAsync(LockName, cancellationToken);
        if (!handle.IsAcquired)
        {
            return new MonthlyResetOutcome(MonthlyResetSkipReasonType.LockHeldElsewhere, configuredDay, today, null);
        }

        var outcome = await quotaReset.ResetAsync(QuotaResetTrigger.Scheduled(), cancellationToken);

        logger.LogInformation(
            "Monthly quota reset for {Period}: {UsersResetCount} active users, {TierChangeCount} tier change(s) moved at the gateway, {TierChangeFailureCount} refused.",
            outcome.Period,
            outcome.UsersResetCount,
            outcome.TierSyncCount,
            outcome.TierSyncFailureCount);

        if (today != configuredDay)
        {
            logger.LogWarning(
                "The {Period} reset ran on the {DayOfMonth} rather than the configured {ConfiguredDayOfMonth} — the scheduled tick(s) in between did not complete. Check earlier invocations for the failure.",
                outcome.Period,
                today,
                configuredDay);
        }

        return new MonthlyResetOutcome(MonthlyResetSkipReasonType.None, configuredDay, today, outcome);
    }

    /// <summary>
    /// Has a scheduled reset already committed for <paramref name="period"/>?
    /// </summary>
    /// <remarks>
    /// The audit row <em>is</em> the record — it is written in the same transaction as the allocations
    /// it describes, so "the row exists" and "the reset committed" are the same fact, with no extra
    /// state to keep in sync. The predicate is a date bound rather than a substring match on the
    /// details JSON: a run inside period P necessarily has an <c>OccurredDate</c> inside P (both come
    /// from the same <c>TimeProvider</c>), <c>OccurredDate</c> is indexed, and matching
    /// <c>"periodYear":2026</c> as text would break the day the serializer's formatting changed. The
    /// details still carry <c>periodYear</c>/<c>periodMonth</c> for whoever reads the trail.
    /// <para>
    /// Only <c>quota.monthly-reset</c> counts. An admin's manual <c>POST /quota/reset</c> the day
    /// before does not suppress the scheduled run: it is a different action with a different actor, and
    /// the scheduled run is what the audit trail is expected to show every month.
    /// </para>
    /// </remarks>
    private Task<bool> AlreadyResetAsync(BillingPeriod period, CancellationToken cancellationToken)
    {
        // Hoisted out of the expression tree: EF cannot translate a method call on the struct.
        var start = period.StartInstant();
        var end = period.EndInstant();

        return dbContext.AuditLogs.AsNoTracking()
            .AnyAsync(
                log => log.Action == AuditActions.QuotaMonthlyReset
                    && log.OccurredDate >= start
                    && log.OccurredDate < end,
                cancellationToken);
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
