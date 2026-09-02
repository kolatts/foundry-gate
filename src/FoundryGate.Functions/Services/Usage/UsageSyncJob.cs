using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Functions.Services.Usage;

/// <summary>
/// Default <see cref="IUsageSyncJob"/>: gateway token log → <c>QuotaAllocation.TokensUsed</c>, in one
/// unit of work.
/// </summary>
public sealed class UsageSyncJob(
    AppDbContext dbContext,
    IUsageQueryClient usageQuery,
    IAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<UsageSyncJob> logger) : IUsageSyncJob
{
    /// <inheritdoc />
    public async Task<UsageSyncOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var period = BillingPeriod.Current(timeProvider);
        var usage = await usageQuery.QueryPeriodUsageAsync(period, cancellationToken);

        var (byUserId, unknownSubscriptions) = MapToUsers(usage);

        var userIds = byUserId.Keys.ToList();
        var allocations = userIds.Count == 0
            ? []
            : await dbContext.QuotaAllocations
                .Where(a => a.PeriodYear == period.Year && a.PeriodMonth == period.Month && userIds.Contains(a.UserId))
                .ToListAsync(cancellationToken);

        var updated = 0;
        var drift = 0;

        foreach (var allocation in allocations)
        {
            var observed = byUserId[allocation.UserId];

            // Assignment, not accumulation: the query returns period totals, which is the whole reason
            // a re-run (or a catch-up after an outage) is harmless.
            if (allocation.TokensUsed != observed.TotalTokens)
            {
                allocation.TokensUsed = observed.TotalTokens;
                updated++;
            }

            // Deliberately NOT setting IsHardStopped: quota exhaustion is the gateway's 403 and this
            // flag means offboarding (#7 direction update). Over-budget usage is instead reported as
            // drift, because the tier product's token-quota should have stopped it at the gateway.
            if (allocation.AllocatedTokens is { } allocated && observed.TotalTokens > allocated)
            {
                drift++;
                logger.LogWarning(
                    "Usage drift for user {UserId} in {Period}: the gateway reported {TokensUsed} tokens against an allocation of {AllocatedTokens} on tier {TierProductId} — {DriftTokens} over. The tier product's token-quota policy should have returned 403; check that the subscription is scoped to the right product.",
                    allocation.UserId,
                    period,
                    observed.TotalTokens,
                    allocated,
                    allocation.TierProductId,
                    observed.TotalTokens - allocated);
            }
        }

        var missingAllocations = userIds.Count - allocations.Count;
        if (missingAllocations > 0)
        {
            // A developer who spent tokens before their first GET /quota/allocations/me of the month and
            // before any reset. Their row appears on the next of either, and this job's next pass fills
            // in the period total — nothing is lost, because the query is cumulative.
            logger.LogInformation(
                "{MissingAllocationCount} developer(s) spent tokens in {Period} with no allocation row yet; their usage lands on the next pass after the row exists.",
                missingAllocations,
                period);
        }

        var outcome = new UsageSyncOutcome(usage.Count, updated, unknownSubscriptions.Count, drift, period);

        // A pass that saw no traffic and changed nothing writes nothing at all. The alternative — one
        // audit row per tick — is 96 rows a day of "nothing happened" burying real admin actions in the
        // audit viewer; the run itself is visible in Application Insights either way.
        if (usage.Count == 0 && updated == 0)
        {
            logger.LogDebug("Usage reconciliation for {Period}: nothing reported and nothing to change.", period);
            return outcome;
        }

        _ = audit.AddSystem(
            AuditActions.UsageSynced,
            string.Empty,
            string.Empty,
            new
            {
                subscriptionsSeen = outcome.SubscriptionsSeen,
                allocationsUpdated = outcome.AllocationsUpdated,
                unknownSubscriptions = outcome.UnknownSubscriptions,
                missingAllocations,
                driftCount = outcome.DriftCount,
                periodYear = period.Year,
                periodMonth = period.Month,
            });

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Usage reconciliation for {Period}: {SubscriptionsSeen} subscription(s) seen, {AllocationsUpdated} allocation(s) updated, {UnknownSubscriptions} unknown, {DriftCount} over budget.",
            period,
            outcome.SubscriptionsSeen,
            outcome.AllocationsUpdated,
            outcome.UnknownSubscriptions,
            outcome.DriftCount);

        return outcome;
    }

    /// <summary>
    /// Turns gateway subscription names into <c>UserId</c>s via
    /// <see cref="ApimSubscriptionNames.TryGetUserId"/>. Anything that is not a FoundryGate-minted name
    /// — APIM's built-in <c>master</c> subscription, one created by hand in the portal — is counted and
    /// logged rather than failing the pass: a fork is allowed to have other consumers on its gateway.
    /// </summary>
    private (Dictionary<int, SubscriptionUsage> ByUserId, List<string> Unknown) MapToUsers(IReadOnlyList<SubscriptionUsage> usage)
    {
        var byUserId = new Dictionary<int, SubscriptionUsage>();
        var unknown = new List<string>();

        foreach (var row in usage)
        {
            if (!ApimSubscriptionNames.TryGetUserId(row.ApimSubscriptionId, out var userId))
            {
                unknown.Add(row.ApimSubscriptionId);
                continue;
            }

            // One user has one subscription, so a duplicate key means the gateway reported the same
            // subscription twice; summing is the only reading that cannot silently lose tokens.
            byUserId[userId] = byUserId.TryGetValue(userId, out var existing)
                ? existing with
                {
                    PromptTokens = existing.PromptTokens + row.PromptTokens,
                    CompletionTokens = existing.CompletionTokens + row.CompletionTokens,
                    TotalTokens = existing.TotalTokens + row.TotalTokens,
                    RequestCount = existing.RequestCount + row.RequestCount,
                }
                : row;
        }

        if (unknown.Count > 0)
        {
            logger.LogInformation(
                "Ignored {UnknownSubscriptionCount} gateway subscription(s) that map to no FoundryGate user: {UnknownSubscriptions}.",
                unknown.Count,
                string.Join(", ", unknown));
        }

        return (byUserId, unknown);
    }
}
