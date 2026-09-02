using FoundryGate.Cli.Helpers;

namespace FoundryGate.Cli.Commands.Ip.Setup;

/// <summary>What <c>ip setup</c> was asked to do, after option parsing.</summary>
/// <param name="Environment">Target environment (<c>--env</c>).</param>
/// <param name="ServerName">Explicit server name (<c>--server</c>), or <see langword="null"/> to resolve by convention.</param>
/// <param name="ResourceGroupName">Explicit resource group (<c>--resource-group</c>), or <see langword="null"/> for <c>rg-foundrygate-{env}</c>.</param>
/// <param name="RuleName">Explicit rule name (<c>--name</c>), or <see langword="null"/> for the convention in <see cref="FirewallRuleNaming"/>.</param>
/// <param name="IpAddress">Explicit IPv4 address (<c>--ip</c>), or <see langword="null"/> to detect the public IP.</param>
/// <param name="SkipConfirmation"><c>--yes</c>.</param>
public sealed record IpSetupRequest(
    string Environment,
    string? ServerName,
    string? ResourceGroupName,
    string? RuleName,
    string? IpAddress,
    bool SkipConfirmation);

/// <summary>
/// The whole of <c>ip setup</c> minus option parsing and process concerns: detect the public IP,
/// resolve the environment's SQL server, and create/update a single-address firewall rule for it —
/// idempotently, so a rule that already points at the same address is left untouched (no ARM write,
/// no needless propagation wait). Modeled on imagile-app's <c>Ip/Setup/SetupCommand.cs</c>, minus
/// Spectre's progress UI (this runs unattended in CI).
/// </summary>
public sealed class IpSetupRunner(
    IAzureSqlServerClient client,
    IPublicIpProvider publicIpProvider,
    RunnerContext runner,
    TimeProvider timeProvider,
    TextWriter output,
    Func<string, bool> confirm)
{
    private readonly IAzureSqlServerClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly IPublicIpProvider _publicIpProvider = publicIpProvider ?? throw new ArgumentNullException(nameof(publicIpProvider));
    private readonly RunnerContext _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly Func<string, bool> _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));

    /// <summary>Runs the command; returns the process exit code (0 = rule in place, 1 = cancelled).</summary>
    public async Task<int> RunAsync(IpSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var environment = FoundryGateAzureResources.NormalizeEnvironment(request.Environment);
        var ipAddress = await ResolveIpAddressAsync(request.IpAddress, cancellationToken);
        var ruleName = ResolveRuleName(request.RuleName);
        var (resourceGroup, server) = await AzureSqlServerResolver.ResolveAsync(
            _client, environment, request.ServerName, request.ResourceGroupName, cancellationToken);

        _output.WriteLine($"Environment:    {environment}");
        _output.WriteLine($"SQL server:     {server.Name} ({server.Fqdn}) in {resourceGroup}");
        _output.WriteLine($"IP address:     {ipAddress}");
        _output.WriteLine($"Firewall rule:  {ruleName}");

        var rules = await _client.ListFirewallRulesAsync(resourceGroup, server.Name, cancellationToken);
        var existing = rules.FirstOrDefault(r => string.Equals(r.Name, ruleName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null && existing.StartIpAddress == ipAddress && existing.EndIpAddress == ipAddress)
        {
            _output.WriteLine($"Firewall rule '{ruleName}' already allows {ipAddress} — nothing to do.");
            return 0;
        }

        var verb = existing is null ? "create" : $"update (currently {existing.StartIpAddress}-{existing.EndIpAddress})";
        if (!request.SkipConfirmation && !_confirm($"This will {verb} firewall rule '{ruleName}' on {server.Name}. Continue?"))
        {
            _output.WriteLine("Cancelled.");
            return 1;
        }

        var result = await _client.CreateOrUpdateFirewallRuleAsync(resourceGroup, server.Name, ruleName, ipAddress, cancellationToken);
        _output.WriteLine(existing is null
            ? $"Created firewall rule '{result.Name}' ({result.StartIpAddress}-{result.EndIpAddress}) on {server.Name}."
            : $"Updated firewall rule '{result.Name}' ({result.StartIpAddress}-{result.EndIpAddress}) on {server.Name}.");

        return 0;
    }

    private string ResolveRuleName(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return FirewallRuleNaming.ForSetup(_runner, _timeProvider.GetUtcNow());
        }

        var trimmed = requested.Trim();
        if (trimmed.Length > FirewallRuleNaming.MaxLength || FirewallRuleNaming.Sanitize(trimmed) != trimmed)
        {
            throw new InvalidOperationException(
                $"Firewall rule name '{requested}' is invalid: use 1-{FirewallRuleNaming.MaxLength} characters from [A-Za-z0-9_.-].");
        }

        return trimmed;
    }

    private async Task<string> ResolveIpAddressAsync(string? requested, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            _output.WriteLine("Detecting public IP address...");
            return (await _publicIpProvider.GetPublicIpAsync(cancellationToken)).ToString();
        }

        if (!HttpPublicIpProvider.TryParseIpv4(requested.Trim(), out var parsed))
        {
            throw new InvalidOperationException($"'{requested}' is not a valid IPv4 address (Azure SQL firewall rules are IPv4-only).");
        }

        return parsed.ToString();
    }
}
