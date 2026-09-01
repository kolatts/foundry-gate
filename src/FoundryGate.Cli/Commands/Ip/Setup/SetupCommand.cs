using System.CommandLine;

namespace FoundryGate.Cli.Commands.Ip.Setup;

/// <summary>
/// STUB. Real implementation is blocked on the Azure SQL Server resource that doesn't exist in
/// this repo yet (it lands with the Bicep infra modules, #43/#44) — see
/// <see href="https://github.com/kolatts/foundry-gate/issues/96">#96</see> for the full plan
/// (detect caller public IP, ArmClient + <c>SqlFirewallRuleResource</c> CreateOrUpdate, modeled on
/// imagile-app's <c>Imagile.App.Cli\Commands\Ip\Setup\SetupCommand.cs</c>).
/// <para>
/// This stub exists purely so <c>.github/workflows/_deploy-database.yml</c> (#79) has the right
/// command shape to call today; that workflow marks the calling step <c>continue-on-error: true</c>
/// with a comment pointing at #96 until this is implemented for real.
/// </para>
/// </summary>
internal sealed class SetupCommand : Command
{
    public SetupCommand() : base("setup", "STUB (see #96): whitelists the caller's public IP on the target environment's Azure SQL Server")
    {
        var envOption = new Option<string>("--env")
        {
            Description = "Target environment (e.g. dev, prod)",
            Required = true
        };

        var yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Accepted for forward compatibility with the reusable deploy workflow; unused by this stub"
        };

        Add(envOption);
        Add(yesOption);

        SetAction(context =>
        {
            var environment = context.GetValue(envOption);

            Console.Error.WriteLine(
                $"'ip setup' is not implemented yet for environment '{environment}'. It needs a real " +
                "Azure SQL Server resource (Bicep infra, #43/#44) before a firewall rule can be " +
                "created against it. See https://github.com/kolatts/foundry-gate/issues/96 for the " +
                "full implementation plan (detect caller public IP -> SqlFirewallRuleResource " +
                "CreateOrUpdate, modeled on imagile-app's Ip/Setup/SetupCommand.cs).");

            return 1;
        });
    }
}
