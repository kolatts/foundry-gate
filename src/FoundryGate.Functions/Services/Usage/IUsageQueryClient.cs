using FoundryGate.Domain.Quota;

namespace FoundryGate.Functions.Services.Usage;

/// <summary>
/// The seam between reconciliation and Azure Monitor Logs (#39/#84): "how many tokens did each APIM
/// subscription spend this billing period?", answered by the checked-in KQL over
/// <c>ApiManagementGatewayLlmLog</c> joined to <c>ApiManagementGatewayLogs</c>.
/// </summary>
/// <remarks>
/// An interface rather than a bare <c>LogsQueryClient</c> so the sync job is testable without a live
/// workspace — there is no local emulator for Log Analytics, and a test that needs one is a test
/// nobody runs. The live half is verified by hand against a deployed gateway; the KQL is checked in
/// so that verification is a copy-paste (#105).
/// </remarks>
public interface IUsageQueryClient
{
    /// <summary>
    /// Per-subscription token totals for <paramref name="period"/> (a whole UTC calendar month, the
    /// same window the gateway's <c>token-quota</c> uses). Totals, not deltas: the caller overwrites
    /// rather than accumulates, which is what makes re-running the job harmless.
    /// </summary>
    /// <returns>One row per APIM subscription that spent tokens; empty when none did.</returns>
    Task<IReadOnlyList<SubscriptionUsage>> QueryPeriodUsageAsync(BillingPeriod period, CancellationToken cancellationToken);
}

/// <summary>One row of the rollup.</summary>
/// <param name="ApimSubscriptionId">The APIM subscription's name/id as the gateway logged it — <c>foundrygate-{UserId}</c> for a FoundryGate developer (<see cref="Domain.Keys.ApimSubscriptionNames"/>), something else for anything created by hand.</param>
/// <param name="PromptTokens">Prompt (input) tokens for the period.</param>
/// <param name="CompletionTokens">Completion (output) tokens for the period.</param>
/// <param name="TotalTokens">Total tokens for the period — what <c>QuotaAllocation.TokensUsed</c> mirrors.</param>
/// <param name="RequestCount">Requests behind those totals, for the log line when something looks wrong.</param>
public sealed record SubscriptionUsage(
    string ApimSubscriptionId,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long RequestCount);
