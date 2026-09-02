namespace FoundryGate.Cli.Helpers;

/// <summary>
/// Turns <c>--env</c> (plus optional <c>--server</c>/<c>--resource-group</c> overrides) into a concrete
/// SQL logical server. The server name carries the deployment's <c>nameSuffix</c>, which the CLI has no
/// way to know, so by default it lists the environment's resource group and matches
/// <see cref="FoundryGateAzureResources.SqlServerNamePrefix"/> — exactly one match is the only acceptable
/// outcome; zero or several is reported as an error rather than guessed at.
/// </summary>
public static class AzureSqlServerResolver
{
    /// <summary>Resolves the target server, or throws an <see cref="InvalidOperationException"/> whose message is fit to print.</summary>
    public static async Task<(string ResourceGroupName, AzureSqlServerInfo Server)> ResolveAsync(
        IAzureSqlServerClient client,
        string environment,
        string? serverName,
        string? resourceGroupName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var resourceGroup = string.IsNullOrWhiteSpace(resourceGroupName)
            ? FoundryGateAzureResources.ResourceGroupName(environment)
            : resourceGroupName.Trim();

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            var server = await client.GetServerAsync(resourceGroup, serverName.Trim(), cancellationToken)
                ?? throw new InvalidOperationException(
                    $"SQL server '{serverName}' was not found in resource group '{resourceGroup}'.");

            return (resourceGroup, server);
        }

        var prefix = FoundryGateAzureResources.SqlServerNamePrefix(environment);
        var servers = await client.ListServersAsync(resourceGroup, cancellationToken);
        var matches = servers
            .Where(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches.Count switch
        {
            1 => (resourceGroup, matches[0]),
            0 => throw new InvalidOperationException(
                $"No SQL server named '{prefix}*' found in resource group '{resourceGroup}'. " +
                "Has the control-plane infra (infra/main.bicep, deployControlPlane=true) been deployed for this environment? " +
                "Pass --server <name> to target a server explicitly."),
            _ => throw new InvalidOperationException(
                $"Found {matches.Count} SQL servers named '{prefix}*' in resource group '{resourceGroup}' " +
                $"({string.Join(", ", matches.Select(m => m.Name))}). Pass --server <name> to pick one.")
        };
    }
}
