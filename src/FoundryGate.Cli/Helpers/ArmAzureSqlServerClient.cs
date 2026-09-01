using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Sql;

namespace FoundryGate.Cli.Helpers;

/// <summary>
/// <see cref="IAzureSqlServerClient"/> over the Azure SDK's <see cref="ArmClient"/>. Firewall-rule writes are
/// plain ARM operations on the server resource (<c>Microsoft.Sql/servers/firewallRules</c>) and need only
/// SQL Server Contributor / Contributor on the resource group — not SQL Entra admin membership, which is
/// what the dacpac deploy itself needs (#109).
/// </summary>
public sealed class ArmAzureSqlServerClient(ArmClient armClient, string? subscriptionId) : IAzureSqlServerClient
{
    /// <summary>Environment variable <c>azure/login@v2</c> callers conventionally export for the target subscription.</summary>
    public const string SubscriptionIdVariable = "AZURE_SUBSCRIPTION_ID";

    private readonly ArmClient _armClient = armClient ?? throw new ArgumentNullException(nameof(armClient));

    /// <summary>Wires the credential chain from <see cref="CliTokenCredential"/> and the subscription from the option or environment.</summary>
    public static ArmAzureSqlServerClient Create(string? subscriptionId)
    {
        var credential = CliTokenCredential.Create();
        var effectiveSubscription = string.IsNullOrWhiteSpace(subscriptionId)
            ? Environment.GetEnvironmentVariable(SubscriptionIdVariable)
            : subscriptionId;

        return new ArmAzureSqlServerClient(new ArmClient(credential), effectiveSubscription);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AzureSqlServerInfo>> ListServersAsync(string resourceGroupName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupName);

        var resourceGroup = await GetResourceGroupAsync(resourceGroupName, cancellationToken);
        var servers = new List<AzureSqlServerInfo>();
        await foreach (var server in resourceGroup.GetSqlServers().GetAllAsync(cancellationToken: cancellationToken))
        {
            servers.Add(ToInfo(server));
        }

        return servers;
    }

    /// <inheritdoc />
    public async Task<AzureSqlServerInfo?> GetServerAsync(string resourceGroupName, string serverName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        var resourceGroup = await GetResourceGroupAsync(resourceGroupName, cancellationToken);
        var response = await resourceGroup.GetSqlServers().GetIfExistsAsync(serverName, cancellationToken: cancellationToken);
        return response.HasValue ? ToInfo(response.Value!) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SqlFirewallRuleInfo>> ListFirewallRulesAsync(string resourceGroupName, string serverName, CancellationToken cancellationToken)
    {
        var server = await GetServerResourceAsync(resourceGroupName, serverName, cancellationToken);
        var rules = new List<SqlFirewallRuleInfo>();
        await foreach (var rule in server.GetSqlFirewallRules().GetAllAsync(cancellationToken))
        {
            rules.Add(ToInfo(rule));
        }

        return rules;
    }

    /// <inheritdoc />
    public async Task<SqlFirewallRuleInfo> CreateOrUpdateFirewallRuleAsync(string resourceGroupName, string serverName, string ruleName, string ipAddress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ipAddress);

        var server = await GetServerResourceAsync(resourceGroupName, serverName, cancellationToken);
        var data = new SqlFirewallRuleData
        {
            StartIPAddress = ipAddress,
            EndIPAddress = ipAddress
        };

        var operation = await server.GetSqlFirewallRules().CreateOrUpdateAsync(WaitUntil.Completed, ruleName, data, cancellationToken);
        return ToInfo(operation.Value);
    }

    /// <inheritdoc />
    public async Task DeleteFirewallRuleAsync(string resourceGroupName, string serverName, string ruleName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);

        var server = await GetServerResourceAsync(resourceGroupName, serverName, cancellationToken);
        var response = await server.GetSqlFirewallRules().GetIfExistsAsync(ruleName, cancellationToken);
        if (!response.HasValue)
        {
            return;
        }

        _ = await response.Value!.DeleteAsync(WaitUntil.Completed, cancellationToken);
    }

    private async Task<ResourceGroupResource> GetResourceGroupAsync(string resourceGroupName, CancellationToken cancellationToken)
    {
        SubscriptionResource subscription;
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            subscription = await _armClient.GetDefaultSubscriptionAsync(cancellationToken);
        }
        else
        {
            subscription = _armClient.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId));
        }

        return await subscription.GetResourceGroupAsync(resourceGroupName, cancellationToken);
    }

    private async Task<SqlServerResource> GetServerResourceAsync(string resourceGroupName, string serverName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        var resourceGroup = await GetResourceGroupAsync(resourceGroupName, cancellationToken);
        return await resourceGroup.GetSqlServers().GetAsync(serverName, cancellationToken: cancellationToken);
    }

    private static AzureSqlServerInfo ToInfo(SqlServerResource server) =>
        new(server.Data.Name, server.Data.FullyQualifiedDomainName);

    private static SqlFirewallRuleInfo ToInfo(SqlFirewallRuleResource rule) =>
        new(rule.Data.Name, rule.Data.StartIPAddress, rule.Data.EndIPAddress);
}
