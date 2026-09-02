namespace FoundryGate.Cli.Helpers;

/// <summary>An Azure SQL logical server as the CLI needs to see it.</summary>
/// <param name="Name">Server name without the DNS suffix, e.g. <c>sql-foundrygate-dev-e7k2</c>.</param>
/// <param name="Fqdn">Fully-qualified host, e.g. <c>sql-foundrygate-dev-e7k2.database.windows.net</c>.</param>
public sealed record AzureSqlServerInfo(string Name, string Fqdn);

/// <summary>A server-level firewall rule (<c>Microsoft.Sql/servers/firewallRules</c>).</summary>
public sealed record SqlFirewallRuleInfo(string Name, string StartIpAddress, string EndIpAddress);

/// <summary>
/// The narrow slice of ARM the <c>ip</c> and <c>db grant-identities</c> commands touch, behind an
/// interface so the command logic is unit-testable with a fake (no live Azure in the Predeployment
/// suite). The one real implementation is <see cref="ArmAzureSqlServerClient"/>.
/// </summary>
public interface IAzureSqlServerClient
{
    /// <summary>Lists the SQL logical servers in a resource group.</summary>
    Task<IReadOnlyList<AzureSqlServerInfo>> ListServersAsync(string resourceGroupName, CancellationToken cancellationToken);

    /// <summary>Gets one server by name, or <see langword="null"/> when it does not exist.</summary>
    Task<AzureSqlServerInfo?> GetServerAsync(string resourceGroupName, string serverName, CancellationToken cancellationToken);

    /// <summary>Lists every firewall rule on a server.</summary>
    Task<IReadOnlyList<SqlFirewallRuleInfo>> ListFirewallRulesAsync(string resourceGroupName, string serverName, CancellationToken cancellationToken);

    /// <summary>Creates or overwrites a single-address rule (start == end == <paramref name="ipAddress"/>).</summary>
    Task<SqlFirewallRuleInfo> CreateOrUpdateFirewallRuleAsync(string resourceGroupName, string serverName, string ruleName, string ipAddress, CancellationToken cancellationToken);

    /// <summary>Deletes a rule; a rule that is already gone is not an error.</summary>
    Task DeleteFirewallRuleAsync(string resourceGroupName, string serverName, string ruleName, CancellationToken cancellationToken);
}
