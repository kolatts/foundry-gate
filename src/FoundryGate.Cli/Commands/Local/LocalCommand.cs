using System.CommandLine;
using FoundryGate.Cli.Commands.Local.Setup;

namespace FoundryGate.Cli.Commands.Local;

/// <summary>Parent command grouping local development environment subcommands.</summary>
internal sealed class LocalCommand : Command
{
    public LocalCommand() : base("local", "Commands for local development environment setup")
    {
        Add(new SetupCommand());
    }
}
