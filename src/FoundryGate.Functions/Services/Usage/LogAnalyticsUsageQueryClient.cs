using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using FoundryGate.Core.Configuration;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Quota;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Functions.Services.Usage;

/// <summary>
/// The real <see cref="IUsageQueryClient"/>: runs <see cref="UsageQueries.PerSubscriptionTokens"/>
/// against the gateway's Log Analytics workspace through the Function App's managed identity
/// (<c>Log Analytics Reader</c> on the workspace — <c>infra/modules/control-plane-rbac.bicep</c>).
/// </summary>
/// <remarks>
/// <para>
/// The workspace is addressed by <b>GUID</b> (<c>Gateway:LogAnalyticsWorkspaceId</c>, the workspace's
/// <c>properties.customerId</c>) because that is what <c>QueryWorkspaceAsync</c> takes; the ARM id
/// infra also sets is a different string and would fail here, which is why
/// <see cref="GatewayOptions"/> validates the shape at startup.
/// </para>
/// <para>
/// The billing period is passed as the query's <see cref="QueryTimeRange"/> rather than baked into
/// the KQL text, so the checked-in file stays parameter-free and an operator running it by hand just
/// picks the same range in the portal. The range is the whole UTC calendar month — the same window
/// the gateway's <c>token-quota</c> counts in.
/// </para>
/// </remarks>
public sealed class LogAnalyticsUsageQueryClient(
    LogsQueryClient logsQuery,
    GatewayOptions gateway,
    ILogger<LogAnalyticsUsageQueryClient> logger) : IUsageQueryClient
{
    /// <summary>Columns <see cref="UsageQueries.PerSubscriptionTokens"/> is contracted to project.</summary>
    private static readonly string[] RequiredColumns =
        ["ApimSubscriptionId", "PromptTokens", "CompletionTokens", "TotalTokens", "RequestCount"];

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubscriptionUsage>> QueryPeriodUsageAsync(BillingPeriod period, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gateway.LogAnalyticsWorkspaceId))
        {
            throw new FeatureNotConfiguredException(
                "Usage reconciliation needs Gateway:LogAnalyticsWorkspaceId (the Log Analytics workspace GUID). infra/modules/control-plane.bicep sets it as Gateway__LogAnalyticsWorkspaceId on the Function App.");
        }

        // The period window is the query's time range, which Log Analytics applies to TimeGenerated
        // server-side — so the scan is bounded by the month, not by the workspace's retention. It is
        // still a whole-month scan on every pass, which is the price of period totals: the alternative
        // is incremental deltas, and a delta the job fails to persist is lost forever, where a total
        // simply gets recomputed. Idempotence is worth more here than a cheaper query.
        var timeRange = new QueryTimeRange(period.StartInstant(), period.EndInstant());

        var response = await logsQuery.QueryWorkspaceAsync(
            gateway.LogAnalyticsWorkspaceId,
            UsageQueries.PerSubscriptionTokens,
            timeRange,
            cancellationToken: cancellationToken);

        var table = response.Value.Table;
        var columns = table.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var required in RequiredColumns)
        {
            if (!columns.Contains(required))
            {
                // GetInt64/GetString throw on an absent column, so this would otherwise surface as an
                // opaque exception per row. The KQL and the table schema have diverged; say which.
                throw new UpstreamDependencyException(
                    $"The usage rollup returned no '{required}' column. Kql/UsageBySubscription.kql and the ApiManagementGatewayLlmLog / ApiManagementGatewayLogs schema have diverged; returned columns: {string.Join(", ", columns)}.");
            }
        }

        var rows = new List<SubscriptionUsage>(table.Rows.Count);

        foreach (var row in table.Rows)
        {
            var subscriptionId = row.GetString("ApimSubscriptionId");
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                // The KQL already filters these out; a row without one means the schema moved.
                logger.LogWarning("Skipping a usage row with no ApimSubscriptionId — check Kql/UsageBySubscription.kql against the current table schema.");
                continue;
            }

            rows.Add(new SubscriptionUsage(
                subscriptionId,
                Tokens(row, "PromptTokens"),
                Tokens(row, "CompletionTokens"),
                Tokens(row, "TotalTokens"),
                Tokens(row, "RequestCount")));
        }

        logger.LogDebug("Log Analytics returned {RowCount} subscription usage row(s) for {Period}.", rows.Count, period);

        return rows;
    }

    /// <summary>A null aggregate reads as zero. An <em>absent</em> column never reaches here — the column check above turns that into one clear failure instead of an opaque per-row throw.</summary>
    private static long Tokens(LogsTableRow row, string column) => row.GetInt64(column) ?? 0;
}
