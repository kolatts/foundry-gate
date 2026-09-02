using System.CommandLine;
using FoundryGate.Cli.Helpers;

namespace FoundryGate.Cli.Commands.Ip.Cleanup;

/// <summary>
/// <c>foundrygate ip cleanup --env &lt;env&gt; [--older-than &lt;hours&gt;] [--dry-run] [--server &lt;name&gt;]
/// [--resource-group &lt;rg&gt;] [--subscription &lt;id&gt;]</c> — removes stale GitHub Actions runner rules
/// (<c>gha-*</c>) from the environment's Azure SQL Server (#96). Called <c>if: always()</c> at the end of
/// <c>_deploy-database.yml</c>; never touches developer or Bicep-declared rules.
/// </summary>
internal sealed class CleanupCommand : Command
{
    /// <summary>Default staleness: comfortably longer than any single deploy, short enough that a crashed run's rule does not linger for days.</summary>
    internal const double DefaultOlderThanHours = 2;

    public CleanupCommand() : base("cleanup", "Removes stale GitHub Actions runner rules (gha-*) from the target environment's Azure SQL Server firewall")
    {
        var envOption = new Option<string>("--env")
        {
            Description = "Target environment (dev, prod; GitHub Environment names like 'production' are accepted)",
            Required = true
        };

        var olderThanOption = new Option<double>("--older-than")
        {
            Description = "Remove gha-* rules created at least this many hours ago (this run's own rules are always removed)",
            DefaultValueFactory = _ => DefaultOlderThanHours
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "List the rules that would be removed without deleting anything"
        };

        var serverOption = new Option<string?>("--server")
        {
            Description = "SQL server name (default: the single sql-foundrygate-{env}-* server in the resource group)"
        };

        var resourceGroupOption = new Option<string?>("--resource-group")
        {
            Description = "Resource group containing the server (default: rg-foundrygate-{env})"
        };

        var subscriptionOption = new Option<string?>("--subscription")
        {
            Description = "Azure subscription id (default: $AZURE_SUBSCRIPTION_ID, then the credential's default subscription)"
        };

        Add(envOption);
        Add(olderThanOption);
        Add(dryRunOption);
        Add(serverOption);
        Add(resourceGroupOption);
        Add(subscriptionOption);

        SetAction(async (parseResult, cancellationToken) =>
        {
            var request = new IpCleanupRequest(
                parseResult.GetValue(envOption)!,
                parseResult.GetValue(serverOption),
                parseResult.GetValue(resourceGroupOption),
                TimeSpan.FromHours(parseResult.GetValue(olderThanOption)),
                parseResult.GetValue(dryRunOption));

            var runner = new IpCleanupRunner(
                ArmAzureSqlServerClient.Create(parseResult.GetValue(subscriptionOption)),
                RunnerContext.FromEnvironment(),
                TimeProvider.System,
                Console.Out);

            try
            {
                _ = await runner.RunAsync(request, cancellationToken);
                return 0;
            }
            catch (Exception ex) when (CliErrors.IsExpected(ex))
            {
                Console.Error.WriteLine($"ip cleanup failed: {CliErrors.Describe(ex)}");
                return 1;
            }
        });
    }
}
