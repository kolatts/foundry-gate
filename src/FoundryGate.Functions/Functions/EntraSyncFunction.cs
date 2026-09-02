using FoundryGate.Functions.Services.Entra;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Functions.Functions;

/// <summary>
/// The nightly Entra directory sync (#151): the users reconciliation, then the group reconciliation,
/// so a fork's roster stops drifting from the directory between admin button presses.
/// </summary>
/// <remarks>
/// <para>
/// <b>02:00 UTC daily</b> (<c>0 0 2 * * *</c>). Overnight for the same reason every directory sync is:
/// the run deprovisions departed developers, which deletes APIM subscriptions, and doing that in the
/// middle of somebody's working day is a worse first impression of an offboarding than doing it while
/// they are asleep. A fixed cron rather than a <c>SystemConfiguration</c> gate like the reset's (#165):
/// the reset has an admin-editable day-of-month key that would otherwise be dead, and this has no
/// equivalent — a fork that wants a different hour edits the expression, which is a redeploy either
/// way.
/// </para>
/// <para>
/// <b><c>RunOnStartup</c> is off</b> and stays off. Every deployment restarts the worker, and a full
/// directory reconciliation — with departure deprovisioning in it — firing on each restart would turn
/// a rollback into an offboarding event.
/// </para>
/// </remarks>
public class EntraSyncFunction(IEntraSyncJob job, ILogger<EntraSyncFunction> logger)
{
    /// <summary>Runs one nightly pass. Exceptions propagate so the Functions host records a failed invocation; the next night retries (both syncs are idempotent).</summary>
    [Function(nameof(EntraSyncFunction))]
    public async Task RunAsync([TimerTrigger("0 0 2 * * *", RunOnStartup = false)] TimerInfo timer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);

        var outcome = await job.RunAsync(cancellationToken);

        logger.LogInformation(
            "Entra sync tick: ran={Ran}, reason={SkipReasonType}, groups={GroupCount}. Next schedule {NextSchedule:u}.",
            outcome.Ran,
            outcome.SkipReasonType,
            outcome.Groups?.Count ?? 0,
            timer.ScheduleStatus?.Next);
    }
}
