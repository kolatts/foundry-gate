using FoundryGate.Domain.Quota;

namespace FoundryGate.Functions.Services.Usage;

/// <summary>
/// Everything <c>UsageSyncFunction</c> does, minus the trigger attribute (#39/#84): read the gateway's
/// own token log for the current billing period and mirror it into
/// <c>QuotaAllocation.TokensUsed</c>. Split out so it is testable against a fake
/// <see cref="IUsageQueryClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reconciliation, not enforcement</b> (CLAUDE.md; #10 direction update). The APIM
/// <c>llm-token-limit</c> policy has already returned <c>403</c> or <c>429</c> in real time, keyed on
/// the developer's subscription; this job exists so the dashboards, the audit trail and cost figures
/// agree with what the gateway did. If it stopped running, nothing about enforcement would change.
/// Accordingly it <b>never sets <c>IsHardStopped</c></b> — that flag means deactivation/offboarding,
/// not "over budget".
/// </para>
/// <para>
/// <b>Idempotent by overwrite.</b> The query returns period <em>totals</em>, so
/// <c>TokensUsed</c> is assigned, never incremented: running the job twice in one window, or catching
/// up after an outage, converges on the same numbers.
/// </para>
/// </remarks>
public interface IUsageSyncJob
{
    /// <summary>Runs one reconciliation pass over the current UTC calendar month.</summary>
    Task<UsageSyncOutcome> RunAsync(CancellationToken cancellationToken);
}

/// <summary>What one pass did — the same fields the run's single <c>usage.synced</c> audit row carries.</summary>
/// <param name="SubscriptionsSeen">Subscriptions the gateway reported usage for.</param>
/// <param name="AllocationsUpdated">Current-period allocations whose <c>TokensUsed</c> changed.</param>
/// <param name="UnknownSubscriptions">Reported subscriptions that map to no FoundryGate user — a hand-made APIM subscription, or a developer deleted since the traffic happened.</param>
/// <param name="DriftCount">
/// Developers whose reconciled usage exceeds their finite allocation. Every one of these is a
/// question about the gateway, not about this job: the tier product's <c>token-quota</c> should have
/// returned <c>403</c> before they got there.
/// </param>
/// <param name="Period">The UTC calendar month reconciled.</param>
public readonly record struct UsageSyncOutcome(
    int SubscriptionsSeen,
    int AllocationsUpdated,
    int UnknownSubscriptions,
    int DriftCount,
    BillingPeriod Period);
