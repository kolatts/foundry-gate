using FoundryGate.Cli.Helpers;

namespace FoundryGate.Tests.Predeployment.Cli;

/// <summary>
/// In-memory <see cref="IAzureSqlServerClient"/>: resource groups → servers → firewall rules, plus a log of
/// every mutating call so tests can assert idempotency ("no write happened") and not just end state.
/// </summary>
public sealed class FakeAzureSqlServerClient : IAzureSqlServerClient
{
    private readonly Dictionary<string, Dictionary<string, AzureSqlServerInfo>> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, SqlFirewallRuleInfo>> _rules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mutating calls in order, e.g. <c>upsert:sql-x/gha-1-202609010900=1.2.3.4</c> or <c>delete:sql-x/gha-1-202609010900</c>.</summary>
    public List<string> Writes { get; } = [];

    public FakeAzureSqlServerClient AddServer(string resourceGroup, string name, params SqlFirewallRuleInfo[] rules)
    {
        if (!_servers.TryGetValue(resourceGroup, out var group))
        {
            group = new Dictionary<string, AzureSqlServerInfo>(StringComparer.OrdinalIgnoreCase);
            _servers[resourceGroup] = group;
        }

        group[name] = new AzureSqlServerInfo(name, $"{name}.database.windows.net");
        _rules[name] = rules.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        return this;
    }

    public IReadOnlyCollection<SqlFirewallRuleInfo> RulesOn(string serverName) => _rules[serverName].Values;

    public Task<IReadOnlyList<AzureSqlServerInfo>> ListServersAsync(string resourceGroupName, CancellationToken cancellationToken)
    {
        if (!_servers.TryGetValue(resourceGroupName, out var group))
        {
            throw new Azure.RequestFailedException(404, $"Resource group '{resourceGroupName}' could not be found.");
        }

        return Task.FromResult<IReadOnlyList<AzureSqlServerInfo>>(group.Values.ToList());
    }

    public Task<AzureSqlServerInfo?> GetServerAsync(string resourceGroupName, string serverName, CancellationToken cancellationToken)
    {
        _servers.TryGetValue(resourceGroupName, out var group);
        AzureSqlServerInfo? server = null;
        _ = group?.TryGetValue(serverName, out server);
        return Task.FromResult(server);
    }

    public Task<IReadOnlyList<SqlFirewallRuleInfo>> ListFirewallRulesAsync(string resourceGroupName, string serverName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SqlFirewallRuleInfo>>(_rules[serverName].Values.ToList());

    public Task<SqlFirewallRuleInfo> CreateOrUpdateFirewallRuleAsync(string resourceGroupName, string serverName, string ruleName, string ipAddress, CancellationToken cancellationToken)
    {
        var rule = new SqlFirewallRuleInfo(ruleName, ipAddress, ipAddress);
        _rules[serverName][ruleName] = rule;
        Writes.Add($"upsert:{serverName}/{ruleName}={ipAddress}");
        return Task.FromResult(rule);
    }

    public Task DeleteFirewallRuleAsync(string resourceGroupName, string serverName, string ruleName, CancellationToken cancellationToken)
    {
        _ = _rules[serverName].Remove(ruleName);
        Writes.Add($"delete:{serverName}/{ruleName}");
        return Task.CompletedTask;
    }
}
