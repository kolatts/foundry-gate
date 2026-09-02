using System.CommandLine;
using FoundryGate.Cli.Commands.Ip.Cleanup;
using FoundryGate.Cli.Commands.Ip.Setup;

namespace FoundryGate.Cli.Commands.Ip;

/// <summary>Parent command grouping Azure SQL Server firewall management subcommands.</summary>
internal sealed class IpCommand : Command
{
    public IpCommand() : base("ip", "Commands to manage Azure SQL Server firewall rules")
    {
        Add(new SetupCommand());
        Add(new CleanupCommand());
    }
}
