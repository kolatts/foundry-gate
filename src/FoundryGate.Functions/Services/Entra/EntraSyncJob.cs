using FoundryGate.Core.Configuration;
using FoundryGate.Core.Entra;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Functions.Services.Jobs;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Functions.Services.Entra;

/// <summary>
/// Default <see cref="IEntraSyncJob"/>: the <c>Entra:Enabled</c> gate, the blob lease (#38's
/// <see cref="IJobLock"/>, a different lock name), Core's two sync services in order, and one audit
/// row for the run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Users first, then groups, and the order is the point.</b> Group sync skips directory members
/// with no <c>User</c> row and counts them as <c>SkippedUnknownUserCount</c>; running the users sync
/// first is what turns those into real memberships on the same night rather than the next one. It is
/// also why the two are one job rather than two timers — two timers could not guarantee the order.
/// </para>
/// <para>
/// <b>A failed users sync stops the pass.</b> The group half would then be reconciling against a
/// roster the directory has already moved on from, and its own skip counts would be misleading. The
/// exception propagates, the Functions host records a failed invocation, and tomorrow's tick retries
/// — both syncs are pull-only and idempotent, so there is nothing to unwind.
/// </para>
/// <para>
/// <b>Departure detection is real here.</b> The users sync deprovisions people the directory no longer
/// lists through <see cref="IDepartureHandler"/>, which on this host is
/// <see cref="DeprovisioningDepartureHandler"/> — the APIM subscription is deleted, not just flagged.
/// A nightly job that marked someone inactive while their gateway key kept working would be worse
/// than one that did nothing, because the admin UI would say they were deprovisioned.
/// </para>
/// </remarks>
public sealed class EntraSyncJob(
    AppDbContext dbContext,
    EntraOptions entra,
    IEntraUserSyncService users,
    IEntraGroupSyncService groups,
    IJobLock jobLock,
    IAuditWriter audit,
    ILogger<EntraSyncJob> logger) : IEntraSyncJob
{
    /// <summary>Name of the lock this job takes; also the lock blob's name. Distinct from the reset's, so the two never block each other.</summary>
    public const string LockName = "entra-directory-sync";

    /// <inheritdoc />
    public async Task<EntraSyncOutcome> RunAsync(CancellationToken cancellationToken)
    {
        if (!entra.Enabled)
        {
            // Information, not Warning or Error (#151): a fork that has not granted the Graph
            // application roles has deliberately left this off, and a nightly error would train
            // everyone to ignore this job's alerts — which is how a real failure gets missed.
            logger.LogInformation(
                "Entra directory sync is disabled on this host (Entra:Enabled is false); nothing to do. Grant the Functions identity the Microsoft Graph application roles Application.Read.All, User.Read.All and GroupMember.ReadBasic.All and set Entra__Enabled to turn it on.");
            return new EntraSyncOutcome(EntraSyncSkipReasonType.DirectoryDisabled, null, null);
        }

        await using var handle = await jobLock.TryAcquireAsync(LockName, cancellationToken);
        if (!handle.IsAcquired)
        {
            return new EntraSyncOutcome(EntraSyncSkipReasonType.LockNotAcquired, null, null);
        }

        var userResult = await users.SyncUsersAsync(cancellationToken);
        var groupResults = await groups.SyncAllAsync(cancellationToken);

        // One row for the run, on top of the users.synced row and the per-group rows the services
        // themselves write: those say what each reconciliation did, this says the schedule ran and how
        // the whole pass came out. Nightly, so 365 rows a year — not the every-15-minutes flood D-016
        // was about.
        _ = audit.AddSystem(
            AuditActions.EntraScheduledSync,
            string.Empty,
            string.Empty,
            new
            {
                users = new
                {
                    userResult.AddedCount,
                    userResult.UpdatedCount,
                    userResult.DeactivatedCount,
                    userResult.SkippedGroupAssignmentCount,
                    userResult.FailedCount,
                },
                groups = new
                {
                    groupCount = groupResults.Count,
                    failedCount = groupResults.Count(result => !result.Succeeded),
                    addedCount = groupResults.Sum(result => result.AddedCount),
                    removedCount = groupResults.Sum(result => result.RemovedCount),
                    skippedUnknownUserCount = groupResults.Sum(result => result.SkippedUnknownUserCount),
                },
            });

        _ = await dbContext.SaveChangesAsync(cancellationToken);

        LogOutcome(userResult, groupResults);

        return new EntraSyncOutcome(EntraSyncSkipReasonType.None, userResult, groupResults);
    }

    private void LogOutcome(Domain.Users.Contracts.UserSyncResult userResult, IReadOnlyList<GroupSyncResult> groupResults)
    {
        var failedGroups = groupResults.Count(result => !result.Succeeded);

        logger.LogInformation(
            "Nightly Entra sync: users {Added} added, {Updated} updated, {Deactivated} deactivated ({FailedUsers} deprovision failure(s), {SkippedGroupAssignments} group assignment(s) unexpanded); groups {GroupCount} reconciled, {FailedGroups} failed.",
            userResult.AddedCount,
            userResult.UpdatedCount,
            userResult.DeactivatedCount,
            userResult.FailedCount,
            userResult.SkippedGroupAssignmentCount,
            groupResults.Count,
            failedGroups);

        if (userResult.SkippedGroupAssignmentCount > 0)
        {
            // #121's partial-view case. Without this line a night on which nobody was deactivated
            // because the directory could not be fully read looks exactly like a night on which nobody
            // left, which is the difference between "quiet" and "not actually checking".
            logger.LogWarning(
                "Nightly Entra sync suspended departure detection: {SkippedGroupAssignments} group app-role assignment(s) could not be expanded, so no user was deactivated on this pass. Nobody has been offboarded by this run — fix the Graph permission (or the deleted group) and the next run deactivates normally.",
                userResult.SkippedGroupAssignmentCount);
        }

        if (userResult.FailedCount > 0 || failedGroups > 0)
        {
            logger.LogError(
                "Nightly Entra sync finished with {FailedUsers} departed user(s) whose deprovision failed and {FailedGroups} group(s) that could not be reconciled. Departed users may still hold a working gateway key until the next run succeeds.",
                userResult.FailedCount,
                failedGroups);
        }
    }
}
