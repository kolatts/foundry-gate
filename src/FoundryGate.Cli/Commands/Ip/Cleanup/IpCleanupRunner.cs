using FoundryGate.Cli.Helpers;

namespace FoundryGate.Cli.Commands.Ip.Cleanup;

/// <summary>What <c>ip cleanup</c> was asked to do, after option parsing.</summary>
/// <param name="Environment">Target environment (<c>--env</c>).</param>
/// <param name="ServerName">Explicit server name (<c>--server</c>), or <see langword="null"/> to resolve by convention.</param>
/// <param name="ResourceGroupName">Explicit resource group (<c>--resource-group</c>), or <see langword="null"/> for <c>rg-foundrygate-{env}</c>.</param>
/// <param name="OlderThan">CI rules created at least this long ago are stale (<c>--older-than</c>, hours).</param>
/// <param name="DryRun">Report without deleting (<c>--dry-run</c>).</param>
public sealed record IpCleanupRequest(
    string Environment,
    string? ServerName,
    string? ResourceGroupName,
    TimeSpan OlderThan,
    bool DryRun);

/// <summary>Which rules <c>ip cleanup</c> removed (or would remove) and which it left alone.</summary>
public sealed record IpCleanupResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Kept);

/// <summary>
/// Prunes GitHub Actions runner rules (<c>gha-*</c>) from the environment's SQL server:
/// <list type="bullet">
/// <item>rules belonging to the <em>current</em> run (<see cref="FirewallRuleNaming.OwnCiRulePrefix"/>) go
/// unconditionally — the run is over, the runner's IP is about to be recycled;</item>
/// <item>other CI rules go once their embedded creation minute is at least <see cref="IpCleanupRequest.OlderThan"/>
/// in the past (a cancelled or crashed earlier run never reached its own cleanup), or when the name carries
/// no parseable timestamp at all (a pre-convention CI rule nothing is going to clean up otherwise);</item>
/// <item>everything else — <c>fg-dev-*</c>, <c>AllowAllWindowsAzureIps</c>, hand-made rules — is never touched.</item>
/// </list>
/// </summary>
public sealed class IpCleanupRunner(
    IAzureSqlServerClient client,
    RunnerContext runner,
    TimeProvider timeProvider,
    TextWriter output)
{
    private readonly IAzureSqlServerClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly RunnerContext _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));

    /// <summary>Runs the command and reports what happened.</summary>
    public async Task<IpCleanupResult> RunAsync(IpCleanupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.OlderThan < TimeSpan.Zero)
        {
            throw new InvalidOperationException("--older-than must be zero or a positive number of hours.");
        }

        var environment = FoundryGateAzureResources.NormalizeEnvironment(request.Environment);
        var (resourceGroup, server) = await AzureSqlServerResolver.ResolveAsync(
            _client, environment, request.ServerName, request.ResourceGroupName, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var ownPrefix = FirewallRuleNaming.OwnCiRulePrefix(_runner);
        _output.WriteLine($"Pruning CI firewall rules on {server.Name} ({resourceGroup}) older than {request.OlderThan.TotalHours:0.##} h" +
                          (ownPrefix is null ? "." : $", plus this run's own '{ownPrefix}*' rules.") +
                          (request.DryRun ? " [dry run]" : string.Empty));

        var rules = await _client.ListFirewallRulesAsync(resourceGroup, server.Name, cancellationToken);
        var removed = new List<string>();
        var kept = new List<string>();

        foreach (var rule in rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!FirewallRuleNaming.IsCiRule(rule.Name))
            {
                kept.Add(rule.Name);
                continue;
            }

            var reason = ClassifyCiRule(rule.Name, ownPrefix, now, request.OlderThan);
            if (reason is null)
            {
                kept.Add(rule.Name);
                continue;
            }

            if (!request.DryRun)
            {
                await _client.DeleteFirewallRuleAsync(resourceGroup, server.Name, rule.Name, cancellationToken);
            }

            removed.Add(rule.Name);
            _output.WriteLine($"  {(request.DryRun ? "would remove" : "removed")} {rule.Name} ({rule.StartIpAddress}) — {reason}");
        }

        _output.WriteLine(removed.Count == 0
            ? "No stale CI firewall rules found."
            : $"{(request.DryRun ? "Would remove" : "Removed")} {removed.Count} CI firewall rule(s); {kept.Count} other rule(s) untouched.");

        return new IpCleanupResult(removed, kept);
    }

    /// <summary>Why a CI rule should go, or <see langword="null"/> to keep it.</summary>
    private static string? ClassifyCiRule(string ruleName, string? ownPrefix, DateTimeOffset now, TimeSpan olderThan)
    {
        if (ownPrefix is not null && ruleName.StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "created by this workflow run";
        }

        var createdAt = FirewallRuleNaming.ParseCiTimestamp(ruleName);
        if (createdAt is null)
        {
            return "CI rule without a creation timestamp";
        }

        var age = now - createdAt.Value;
        return age >= olderThan ? $"created {age.TotalHours:0.#} h ago" : null;
    }
}
