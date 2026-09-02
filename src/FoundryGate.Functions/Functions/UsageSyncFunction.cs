using FoundryGate.Domain.Exceptions;
using FoundryGate.Functions.Services.Usage;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Functions.Functions;

/// <summary>
/// Usage reconciliation every 15 minutes (#39/#84): the gateway's <c>ApiManagementGatewayLlmLog</c>
/// rollup mirrored into <c>QuotaAllocation.TokensUsed</c>.
/// </summary>
/// <remarks>
/// <b>Reconciliation, not enforcement.</b> The gateway has already returned <c>403</c>/<c>429</c> in
/// real time; this job only makes the dashboards and the audit trail agree with it. Fifteen minutes
/// is therefore a freshness choice, not a correctness one — see <see cref="IUsageSyncJob"/>.
/// </remarks>
public class UsageSyncFunction(IUsageSyncJob job, ILogger<UsageSyncFunction> logger)
{
    /// <summary>Runs one reconciliation pass.</summary>
    [Function(nameof(UsageSyncFunction))]
    public async Task RunAsync([TimerTrigger("0 */15 * * * *", RunOnStartup = false)] TimerInfo timer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);

        try
        {
            var outcome = await job.RunAsync(cancellationToken);

            logger.LogDebug(
                "Usage sync tick for {Period}: {SubscriptionsSeen} seen, {AllocationsUpdated} updated. Next schedule {NextSchedule:u}.",
                outcome.Period,
                outcome.SubscriptionsSeen,
                outcome.AllocationsUpdated,
                timer.ScheduleStatus?.Next);
        }
        catch (FeatureNotConfiguredException exception)
        {
            // A fork without the GenAI diagnostic setting has nothing to reconcile against. That is a
            // configuration state, not a fault: logging it once per tick beats a failed invocation
            // every 15 minutes, which would bury real failures in the same alert.
            logger.LogWarning(exception, "Usage reconciliation is not configured; skipping this pass.");
        }
    }
}
