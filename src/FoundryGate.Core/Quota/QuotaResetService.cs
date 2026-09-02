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
/// the allocations and the run's single audit row commit together.
/// </summary>
public sealed class QuotaResetService(
    AppDbContext dbContext,
    IQuotaResolutionService quotaResolution,
    IAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<QuotaResetService> logger) : IQuotaResetService
{
    /// <inheritdoc />
    public async Task<QuotaResetOutcome> ResetAsync(QuotaResetTrigger trigger, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var period = BillingPeriod.FromInstant(now);

        var activeUserIds = await dbContext.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.UserId)
            .Select(u => u.UserId)
            .ToListAsync(cancellationToken);

        var resolutions = await quotaResolution.ResolveManyAsync(activeUserIds, period, cancellationToken);
        var touched = resolutions.Select(r => r.Allocation).ToList();

        // Past the commit point once any subscription has been re-scoped at the gateway. A mid-loop ARM
        // failure aborts before this line and saves nothing (the tier sync adds its audit row without
        // saving, #156 review), so the reset is all-or-nothing exactly as the interface promises.
        // The predicate is "we reached APIM", not "we called something that might have": a reset over
        // unchanged inputs moves nobody and is not a commit point (CONVENTIONS.md; #184).
        var tierSyncCount = resolutions.Count(r => r.TierSyncRequested);
        var completionToken = CommitToken.For(tierSyncCount > 0, cancellationToken);

        foreach (var allocation in touched)
        {
            Stamp(allocation, now);
        }

        // One row per run, no single target (CONVENTIONS.md: empty target when there is none).
        // Added before the save so it commits atomically with every allocation it describes.
        // periodYear/periodMonth are load-bearing, not decoration: the scheduled job reads them back to
        // answer "has this period already been reset?" so a missed day is not a missed month (#38).
        var details = new
        {
            usersResetCount = resolutions.Count,
            periodYear = period.Year,
            periodMonth = period.Month,
            // Named for what it means to a human reading the trail, not for the seam it went through:
            // every one of these is a developer whose enforced budget moved this run.
            tierChangeCount = tierSyncCount,
        };

        _ = trigger.ActorUserId is { } actorUserId
            ? audit.Add(actorUserId, trigger.AuditAction, string.Empty, string.Empty, details)
            : audit.AddSystem(trigger.AuditAction, string.Empty, string.Empty, details);

        try
        {
            await dbContext.SaveChangesAsync(completionToken);
        }
        catch (DbUpdateException exception) when (resolutions.Any(r => r.IsNew))
        {
            // A concurrent reset (or a developer's first /me of the month) inserted some of the rows we
            // were about to Add. Adopt the winners — re-apply our resolution to their rows — and save
            // again; a failed SaveChanges leaves every entry (including the audit row) still pending, so
            // the second save is the same atomic unit. Anything other than a lost race is rethrown.
            var adopted = await AdoptConcurrentlyCreatedRowsAsync(touched, period, now, completionToken);
            if (adopted == 0)
            {
                throw;
            }

            logger.LogInformation(exception, "Quota reset for {Period} raced a concurrent writer on {AdoptedCount} allocation(s); adopted the existing rows.", period, adopted);
            await dbContext.SaveChangesAsync(completionToken);
        }

        logger.LogInformation(
            "Quota reset for {Period} ({AuditAction}): {UsersResetCount} active users, {TierChangeCount} tier change(s).",
            period,
            trigger.AuditAction,
            resolutions.Count,
            tierSyncCount);

        return new QuotaResetOutcome(resolutions.Count, tierSyncCount, period, now);
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
            dbContext.QuotaAllocations.Update(winner);

            touched[i] = winner;
            adopted++;
        }

        return adopted;
    }
}
