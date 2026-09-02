using FoundryGate.Functions.Services.Usage;

namespace FoundryGate.Tests.Predeployment.Functions.Services.Usage;

/// <summary>
/// The KQL is a checked-in file, not a string literal (#84's acceptance list), which means the build
/// has to keep embedding it and the text has to keep naming the two tables the join depends on.
/// A rename or a dropped <c>EmbeddedResource</c> item fails here rather than at 15-minute intervals in
/// production.
/// </summary>
public class UsageQueriesTests
{
    [Fact]
    public void The_per_subscription_query_loads_from_the_embedded_kql_file()
    {
        Assert.False(string.IsNullOrWhiteSpace(UsageQueries.PerSubscriptionTokens));
    }

    [Theory]
    [InlineData("ApiManagementGatewayLlmLog")]   // per-request token counts
    [InlineData("ApiManagementGatewayLogs")]     // the only table carrying ApimSubscriptionId
    [InlineData("CorrelationId")]                // the join key between them
    [InlineData("ApimSubscriptionId")]           // the column the sync job maps back to a user
    public void The_query_names_the_tables_and_columns_reconciliation_depends_on(string expected)
    {
        Assert.Contains(expected, UsageQueries.PerSubscriptionTokens, StringComparison.Ordinal);
    }

    [Fact]
    public void The_query_carries_no_time_filter_of_its_own_because_the_caller_passes_a_QueryTimeRange()
    {
        // If someone adds `| where TimeGenerated ...` here it would silently intersect with the range
        // LogAnalyticsUsageQueryClient passes, and the totals would quietly become partial.
        Assert.DoesNotContain("TimeGenerated >", UsageQueries.PerSubscriptionTokens, StringComparison.Ordinal);
        Assert.DoesNotContain("ago(", UsageQueries.PerSubscriptionTokens, StringComparison.Ordinal);
    }
}
