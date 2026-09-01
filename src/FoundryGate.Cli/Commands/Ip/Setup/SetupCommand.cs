using System.CommandLine;
using FoundryGate.Cli.Helpers;

namespace FoundryGate.Cli.Commands.Ip.Setup;

/// <summary>
/// <c>foundrygate ip setup --env &lt;env&gt; [--yes] [--name &lt;rule&gt;] [--ip &lt;addr&gt;] [--server &lt;name&gt;]
/// [--resource-group &lt;rg&gt;] [--subscription &lt;id&gt;]</c> — whitelists the caller's public IP on the
/// environment's Azure SQL Server (#96). Thin System.CommandLine shell around <see cref="IpSetupRunner"/>;
/// the Azure/HTTP edges are the only things constructed here.
/// </summary>
internal sealed class SetupCommand : Command
{
    public SetupCommand() : base("setup", "Whitelists the caller's public IP on the target environment's Azure SQL Server firewall")
    {
        var envOption = new Option<string>("--env")
        {
            Description = "Target environment (dev, prod; GitHub Environment names like 'production' are accepted)",
            Required = true
        };

        var yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Skip the confirmation prompt (required when stdin is not a terminal, e.g. CI)"
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Firewall rule name (default: gha-{run id}-{UTC minute} on GitHub Actions, fg-dev-{machine}-{user} otherwise)"
        };

        var ipOption = new Option<string?>("--ip")
        {
            Description = "IPv4 address to whitelist (default: detected via api.ipify.org / ifconfig.me)"
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
        Add(yesOption);
        Add(nameOption);
        Add(ipOption);
        Add(serverOption);
        Add(resourceGroupOption);
        Add(subscriptionOption);

        SetAction(async (parseResult, cancellationToken) =>
        {
            var request = new IpSetupRequest(
                parseResult.GetValue(envOption)!,
                parseResult.GetValue(serverOption),
                parseResult.GetValue(resourceGroupOption),
                parseResult.GetValue(nameOption),
                parseResult.GetValue(ipOption),
                parseResult.GetValue(yesOption));

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var runner = new IpSetupRunner(
                ArmAzureSqlServerClient.Create(parseResult.GetValue(subscriptionOption)),
                new HttpPublicIpProvider(httpClient),
                RunnerContext.FromEnvironment(),
                TimeProvider.System,
                Console.Out,
                ConsolePrompts.Confirm);

            try
            {
                return await runner.RunAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Azure.RequestFailedException)
            {
                Console.Error.WriteLine($"ip setup failed: {ex.Message}");
                return 1;
            }
        });
    }
}
