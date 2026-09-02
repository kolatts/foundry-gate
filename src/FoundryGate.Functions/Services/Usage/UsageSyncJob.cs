using System.Text.Json;
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
    /// <summary>
    /// How far into a new month the just-closed period keeps being reconciled as well.
    /// </summary>
    /// <remarks>
    /// The last pass of a month runs at 23:45 and sees only what Log Analytics had ingested by then;
    /// everything after that — the final quarter-hour of traffic, plus ingestion lag, which Azure
    /// Monitor does not bound tightly — would otherwise be missing from that month forever, because
    /// <c>BillingPeriod.Current</c> never looks back. Three days is generous against documented
    /// latency and costs 288 extra queries against a period whose row count has stopped growing.
    /// </remarks>
    public const int PreviousPeriodGraceDays = 3;

    /// <inheritdoc />
    public async Task<UsageSyncOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var current = BillingPeriod.FromInstant(now);

        var periods = new List<BillingPeriod> { current };
        if (now.UtcDateTime.Day <= PreviousPeriodGraceDays)
        {
            periods.Add(current.Previous());
        }

        var totals = new PeriodTotals();
        foreach (var period in periods)
        {
            totals = totals.Add(await ReconcilePeriodAsync(period, cancellationToken));
        }

        var outcome = new UsageSyncOutcome(
            totals.SubscriptionsSeen,
            totals.AllocationsUpdated,
            totals.UnknownSubscriptions,
            totals.DriftCount,
            current,
            periods.Count > 1);

        if (!await ShouldAuditAsync(outcome, totals.MissingAllocations, cancellationToken))
        {
            // Nothing moved and nothing new is wrong. At 96 ticks a day, auditing this anyway is ~35k
            // rows a year of "same as last time" burying every real admin action in the audit viewer;
            // the pass itself is visible in Application Insights either way (D-016).
            logger.LogDebug("Usage reconciliation for {Periods}: nothing changed since the last recorded pass.", string.Join(", ", periods));
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
                missingAllocations = totals.MissingAllocations,
                driftCount = outcome.DriftCount,
                periodYear = current.Year,
                periodMonth = current.Month,
                periodsReconciled = periods.Select(p => p.ToString()).ToList(),
            });

        // The caller's own token, deliberately: no CommitToken here because reconciliation has no commit
        // point. Its only external call is a *read* of Log Analytics — nothing outside the database has
        // accepted a change that the database now owes a record of, so an abandoned pass should stop,
        // and the next tick recomputes the same totals anyway (CONVENTIONS.md).
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Usage reconciliation for {Periods}: {SubscriptionsSeen} subscription(s) seen, {AllocationsUpdated} allocation(s) updated, {UnknownSubscriptions} unknown, {DriftCount} over budget.",
            string.Join(", ", periods),
            outcome.SubscriptionsSeen,
            outcome.AllocationsUpdated,
            outcome.UnknownSubscriptions,
            outcome.DriftCount);

        return outcome;
    }

    /// <summary>Reconciles one period onto the change tracker. Saves nothing — the caller owns the single commit.</summary>
    private async Task<PeriodTotals> ReconcilePeriodAsync(BillingPeriod period, CancellationToken cancellationToken)
    {
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

        return new PeriodTotals(usage.Count, updated, unknownSubscriptions.Count, drift, missingAllocations);
    }

    /// <summary>
    /// Is there anything worth a row in the audit trail? Yes when a <c>TokensUsed</c> value actually
    /// moved, and yes when the counts that describe a <em>problem</em> — unknown subscriptions, drift,
    /// developers with no allocation row — differ from the last recorded pass.
    /// </summary>
    /// <remarks>
    /// The last-pass comparison is what makes "new" mean something. Gating on the raw counts instead
    /// would write a row every 15 minutes for as long as a single unknown subscription existed, which
    /// is the same flood with extra steps; gating on <c>updated</c> alone would silently swallow the
    /// first pass on which an admin's quota cut put someone over budget without their usage changing.
    /// The previous run's own details JSON is the state — we wrote it, so parsing it back is not a
    /// contract with anyone else — and anything unreadable is treated as "different", which fails
    /// towards recording rather than towards silence.
    /// </remarks>
    private async Task<bool> ShouldAuditAsync(UsageSyncOutcome outcome, int missingAllocations, CancellationToken cancellationToken)
    {
        if (outcome.AllocationsUpdated > 0)
        {
            return true;
        }

        var previous = await dbContext.AuditLogs.AsNoTracking()
            .Where(log => log.Action == AuditActions.UsageSynced)
            .OrderByDescending(log => log.OccurredDate)
            .ThenByDescending(log => log.AuditLogId)
            .Select(log => log.Details)
            .FirstOrDefaultAsync(cancellationToken);

        if (previous is null)
        {
            // No pass has ever been recorded. Write one even if it is all zeroes: the first row is what
            // tells an operator the job is wired up at all.
            return true;
        }

        return !MatchesPreviousCounts(previous, outcome, missingAllocations);
    }

    /// <summary>True when the previous <c>usage.synced</c> row describes exactly the same problem counts.</summary>
    private bool MatchesPreviousCounts(string previousDetails, UsageSyncOutcome outcome, int missingAllocations)
    {
        try
        {
            using var document = JsonDocument.Parse(previousDetails);
            var root = document.RootElement;

            return Count(root, "unknownSubscriptions") == outcome.UnknownSubscriptions
                && Count(root, "driftCount") == outcome.DriftCount
                && Count(root, "missingAllocations") == missingAllocations;
        }
        catch (JsonException exception)
        {
            logger.LogInformation(exception, "Could not read the previous usage.synced details; recording this pass rather than assuming it is a repeat.");
            return false;
        }

        static int? Count(JsonElement root, string property) =>
            root.TryGetProperty(property, out var value) && value.TryGetInt32(out var count) ? count : null;
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

    /// <summary>Running totals across the one or two periods a pass reconciles.</summary>
    private readonly record struct PeriodTotals(
        int SubscriptionsSeen,
        int AllocationsUpdated,
        int UnknownSubscriptions,
        int DriftCount,
        int MissingAllocations)
    {
        public PeriodTotals Add(PeriodTotals other) => new(
            SubscriptionsSeen + other.SubscriptionsSeen,
            AllocationsUpdated + other.AllocationsUpdated,
            UnknownSubscriptions + other.UnknownSubscriptions,
            DriftCount + other.DriftCount,
            MissingAllocations + other.MissingAllocations);
    }
}
