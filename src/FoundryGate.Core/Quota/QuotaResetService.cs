using FoundryGate.Core.Requests;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Concurrency;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Core.Quota;

/// <summary>
/// Default <see cref="IQuotaResetService"/>. Scoped: shares the caller's <see cref="AppDbContext"/>, so
/// the allocations and the run's audit row commit together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is no longer a single transaction (#211 review).</b> Since #194 a reset can move a
/// developer's APIM subscription, and a batch that moves subscriptions inside its loop and saves once
/// at the end cannot be all-or-nothing: by the time user <c>N</c>'s move fails, users <c>1..N-1</c> are
/// already re-scoped at the gateway and the rollback throws away the rows describing them — SQL says
/// the old tier, APIM enforces the new one, and nothing recorded it. So resolution now runs with the
/// gateway <see cref="GatewayTierSyncMode.Deferred"/>, and this service performs each move itself and
/// commits it immediately (CONVENTIONS.md §External side effects have a commit point). One developer's
/// refused move costs that developer's allocation update and nothing else.
/// </para>
/// <para>
/// <b>Developers whose tier is not moving still share one save.</b> They reach no external system, so
/// there is nothing to commit early and nothing a rollback would strand; they ride the final save
/// together with the run's own audit row, exactly as before.
/// </para>
/// <para>
/// <b>A refused move is reported, not fatal.</b> It is logged at Error with the developer's full
/// identity, counted into <see cref="QuotaResetOutcome.TierSyncFailureCount"/> and the run's audit row,
/// and the reset carries on. That developer's allocation stays held — no row for this period — because
/// writing one would claim a budget the gateway is not enforcing; their previous period's row still
/// matches the gateway and is untouched. The alternative — abort — meant one subscription deleted out
/// of band in the APIM portal deterministically failed every developer's reset, on every retry.
/// </para>
/// </remarks>
public sealed class QuotaResetService(
    AppDbContext dbContext,
    IQuotaResolutionService quotaResolution,
    IGatewayTierSync tierSync,
    IQuotaRequestExpiry requestExpiry,
    IAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<QuotaResetService> logger) : IQuotaResetService
{
    /// <inheritdoc />
    public async Task<QuotaResetOutcome> ResetAsync(QuotaResetTrigger trigger, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var period = BillingPeriod.FromInstant(now);

        // The entities, not just the ids: IGatewayTierSync takes a User, and EF's identity resolution
        // means these are the same instances resolution reads.
        var activeUsers = await dbContext.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.UserId)
            .ToListAsync(cancellationToken);

        // The queue is swept as part of the reset (#159): a request nobody reviewed before the period
        // closed can no longer raise anything (approval refuses it), so leaving it Pending only means an
        // admin opens a review queue every month with last month's dead entries still in it. Purely a
        // database change with its own audit row, added to this same unit of work.
        //
        // Deliberately above the commit point (#204 review): its rows commit with the reset either way,
        // but running its query after the first tier move would mean a transient database failure here
        // discards a reset APIM has already accepted — the exact divergence CommitToken exists to close.
        // Up here, a failure aborts before anything external has happened and costs nothing.
        var expiredRequestCount = await requestExpiry.ExpireStaleAsync(period, cancellationToken);

        // Deferred: this pass reaches no gateway, so nothing is past a commit point yet and every
        // allocation it staged is still ours to keep or discard.
        var resolutions = await quotaResolution.ResolveManyAsync(
            [.. activeUsers.Select(user => user.UserId)],
            period,
            GatewayTierSyncMode.Deferred,
            cancellationToken);

        var usersById = activeUsers.ToDictionary(user => user.UserId);
        var movers = resolutions.Where(resolution => resolution.TierSyncRequired).ToList();

        // Hold every moving developer's row out of the change tracker. SaveChangesAsync commits the
        // whole tracker, so without this the first successful move would also commit the rows of
        // developers whose own move has not been attempted yet — each of them then claiming a tier the
        // gateway is not enforcing if their move later fails. Restored the moment their move lands.
        foreach (var mover in movers)
        {
            Hold(mover);
        }

        var touched = new List<QuotaAllocation>(resolutions.Count);
        foreach (var settled in resolutions.Where(resolution => !resolution.TierSyncRequired))
        {
            Stamp(settled.Allocation, now);
            touched.Add(settled.Allocation);
        }

        var tierSyncCount = 0;
        var tierSyncFailureCount = 0;

        foreach (var mover in movers)
        {
            var user = usersById[mover.Allocation.UserId];
            var tierProductId = mover.Allocation.TierProductId;

            try
            {
                await tierSync.SyncAsync(user, tierProductId, mover.PreviousTierProductId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The gateway refused this one developer. Their row stays detached — no allocation for
                // this period rather than one claiming a budget nothing enforces — so the database goes
                // on recording only the tier APIM still has, and the run carries on for everybody else.
                tierSyncFailureCount++;
                logger.LogError(
                    exception,
                    "Quota reset for {Period}: the gateway did not accept moving user {UserId} ({Email}, subscription {ApimSubscriptionId}) from tier {PreviousTierProductId} to {TierProductId}. Their allocation was left recording the previous tier, which is what the gateway still enforces; the rest of the run continues and the next reset retries.",
                    period,
                    user.UserId,
                    user.Email,
                    user.ApimSubscriptionId,
                    mover.PreviousTierProductId ?? "(none)",
                    tierProductId);
                continue;
            }

            // ---- commit point: APIM has re-scoped this developer's subscription, and the
            // key.tier-changed row the sync staged now owes the database a record. From here the save
            // runs on CommitToken.For(true, …), so an abandoned reset cannot turn an accepted move into
            // an unaudited one.
            Restore(mover);
            Stamp(mover.Allocation, now);
            touched.Add(mover.Allocation);
            tierSyncCount++;

            await SaveAsync(touched, period, now, new CommittedMove(user, tierProductId), CommitToken.For(true, cancellationToken));
        }

        // One row per run, no single target (CONVENTIONS.md: empty target when there is none).
        // periodYear/periodMonth are load-bearing, not decoration: the scheduled job reads them back to
        // answer "has this period already been reset?" so a missed day is not a missed month (#38).
        var details = new
        {
            usersResetCount = touched.Count,
            periodYear = period.Year,
            periodMonth = period.Month,
            // Named for what they mean to a human reading the trail, not for the seam they went through:
            // developers whose enforced budget moved this run, and developers whose move the gateway
            // refused — the second is the number that should make somebody look.
            tierChangeCount = tierSyncCount,
            tierChangeFailureCount = tierSyncFailureCount,
            // Zero on almost every run; carried anyway so "the queue was swept and found nothing" and
            // "the sweep did not run" are distinguishable from the reset's own row.
            expiredRequestCount,
        };

        _ = trigger.ActorUserId is { } actorUserId
            ? audit.Add(actorUserId, trigger.AuditAction, string.Empty, string.Empty, details)
            : audit.AddSystem(trigger.AuditAction, string.Empty, string.Empty, details);

        // The final save carries the run's own row plus every developer who needed no gateway move. Once
        // any move has landed the run itself has consequences at the gateway and its audit row is owed
        // to the database, so the commit point covers this save too (#163); a reset that moved nobody
        // reached nothing external and keeps the caller's token.
        await SaveAsync(touched, period, now, move: null, CommitToken.For(tierSyncCount > 0, cancellationToken));

        if (tierSyncFailureCount > 0)
        {
            logger.LogWarning(
                "Quota reset for {Period} ({AuditAction}): {UsersResetCount} active users, {TierChangeCount} tier change(s), {TierChangeFailureCount} refused by the gateway and left un-moved, {ExpiredRequestCount} stale request(s) closed.",
                period,
                trigger.AuditAction,
                touched.Count,
                tierSyncCount,
                tierSyncFailureCount,
                expiredRequestCount);
        }
        else
        {
            logger.LogInformation(
                "Quota reset for {Period} ({AuditAction}): {UsersResetCount} active users, {TierChangeCount} tier change(s), {ExpiredRequestCount} stale request(s) closed.",
                period,
                trigger.AuditAction,
                touched.Count,
                tierSyncCount,
                expiredRequestCount);
        }

        return new QuotaResetOutcome(touched.Count, tierSyncCount, tierSyncFailureCount, expiredRequestCount, period, now);
    }

    /// <summary>
    /// Saves whatever is staged, adopting rows a concurrent writer inserted underneath us.
    /// </summary>
    /// <param name="touched">Every allocation currently staged, so the adoption pass can find the Added ones.</param>
    /// <param name="period">The period being reset.</param>
    /// <param name="now">The instant every touched row records.</param>
    /// <param name="move">
    /// The accepted gateway move this save is recording, when there is one. A failure here is the
    /// residual orphan CONVENTIONS.md describes — APIM moved, the database did not record it — so it is
    /// logged at Error with the full identity before it propagates.
    /// </param>
    /// <param name="cancellationToken">The token this unit of work finishes on.</param>
    private async Task SaveAsync(List<QuotaAllocation> touched, BillingPeriod period, DateTimeOffset now, CommittedMove? move, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (touched.Any(a => dbContext.Entry(a).State == EntityState.Added))
        {
            // A concurrent reset (or a developer's first /me of the month) inserted some of the rows we
            // were about to Add. Adopt the winners — re-apply our resolution to their rows — and save
            // again; a failed SaveChanges leaves every entry (including the audit row) still pending, so
            // the second save is the same atomic unit. Anything other than a lost race is rethrown.
            var adopted = await AdoptConcurrentlyCreatedRowsAsync(touched, period, now, cancellationToken);
            if (adopted == 0)
            {
                LogOrphan(move, exception, period);
                throw;
            }

            logger.LogInformation(exception, "Quota reset for {Period} raced a concurrent writer on {AdoptedCount} allocation(s); adopted the existing rows.", period, adopted);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception retryException)
            {
                LogOrphan(move, retryException, period);
                throw;
            }
        }
        catch (Exception exception)
        {
            LogOrphan(move, exception, period);
            throw;
        }
    }

    /// <summary>The Error log CONVENTIONS.md asks for when a save fails after an external system has accepted the change it was recording.</summary>
    private void LogOrphan(CommittedMove? move, Exception exception, BillingPeriod period)
    {
        if (move is null)
        {
            return;
        }

        logger.LogError(
            exception,
            "Quota reset for {Period}: the gateway moved user {UserId} ({Email}, subscription {ApimSubscriptionId}) to tier {TierProductId}, but the allocation and its key.tier-changed row could not be saved. The gateway is now enforcing a tier the database does not record — reconcile this developer by hand.",
            period,
            move.User.UserId,
            move.User.Email,
            move.User.ApimSubscriptionId,
            move.TierProductId);
    }

    /// <summary>A gateway move APIM has already accepted, so the save recording it can name it if it fails.</summary>
    /// <param name="User">The developer whose subscription moved.</param>
    /// <param name="TierProductId">The tier product it moved to.</param>
    private sealed record CommittedMove(User User, string TierProductId);

    /// <summary>Detaches a resolved-but-not-yet-moved allocation so no other developer's save can commit it.</summary>
    private void Hold(QuotaResolution resolution) =>
        dbContext.Entry(resolution.Allocation).State = EntityState.Detached;

    /// <summary>
    /// Re-attaches a held allocation now that its gateway move has landed, marking exactly the columns
    /// resolution and <see cref="Stamp"/> write.
    /// </summary>
    /// <remarks>
    /// Property-level rather than <c>Update()</c> or <c>State = Modified</c> on purpose: those mark every
    /// column dirty, including <c>TokensUsed</c>, which belongs to reconciliation (#39) and would be
    /// written back from a value this reset read minutes earlier.
    /// </remarks>
    private void Restore(QuotaResolution resolution)
    {
        var allocation = resolution.Allocation;

        if (resolution.IsNew)
        {
            _ = dbContext.QuotaAllocations.Add(allocation);
            return;
        }

        var entry = dbContext.QuotaAllocations.Attach(allocation);
        entry.Property(a => a.AllocatedTokens).IsModified = true;
        entry.Property(a => a.ResolvedLevelType).IsModified = true;
        entry.Property(a => a.TierProductId).IsModified = true;
        entry.Property(a => a.IsGatewayCapped).IsModified = true;
        entry.Property(a => a.IsHardStopped).IsModified = true;
        entry.Property(a => a.ResetDate).IsModified = true;
    }

    /// <summary>Every touched row starts the period un-stopped and records when it was last resolved.</summary>
    private static void Stamp(QuotaAllocation allocation, DateTimeOffset now)
    {
        // IsHardStopped is the offboarding/revocation mirror, not the quota-exhaustion one (#7
        // direction update) — clearing it here is how a period starts clean on the dashboards.
        allocation.IsHardStopped = false;
        allocation.ResetDate = now;
    }

    /// <summary>
    /// For every allocation in <paramref name="touched"/> that is still <see cref="EntityState.Added"/>
    /// but whose (user, period) row now exists in the database: detaches ours, copies the resolution
    /// outputs onto the winner (which keeps its <c>TokensUsed</c>, exactly as a re-resolve would), stamps
    /// it, and swaps it into <paramref name="touched"/>. Returns how many rows were adopted.
    /// </summary>
    private async Task<int> AdoptConcurrentlyCreatedRowsAsync(List<QuotaAllocation> touched, BillingPeriod period, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pendingIds = touched
            .Where(a => dbContext.Entry(a).State == EntityState.Added)
            .Select(a => a.UserId)
            .ToList();
        if (pendingIds.Count == 0)
        {
            return 0;
        }

        var winners = await dbContext.QuotaAllocations.AsNoTracking()
            .Where(a => pendingIds.Contains(a.UserId) && a.PeriodYear == period.Year && a.PeriodMonth == period.Month)
            .ToDictionaryAsync(a => a.UserId, cancellationToken);

        var adopted = 0;
        for (var i = 0; i < touched.Count; i++)
        {
            var ours = touched[i];
            if (dbContext.Entry(ours).State != EntityState.Added || !winners.TryGetValue(ours.UserId, out var winner))
            {
                continue;
            }

            dbContext.Entry(ours).State = EntityState.Detached;

            winner.AllocatedTokens = ours.AllocatedTokens;
            winner.ResolvedLevelType = ours.ResolvedLevelType;
            winner.TierProductId = ours.TierProductId;
            winner.IsGatewayCapped = ours.IsGatewayCapped;
            Stamp(winner, now);
            _ = dbContext.QuotaAllocations.Update(winner);

            touched[i] = winner;
            adopted++;
        }

        return adopted;
    }
}
