using System.CommandLine;
using FoundryGate.Cli.Commands.Db.Compare;
using FoundryGate.Cli.Commands.Db.Deploy;
using FoundryGate.Cli.Commands.Db.GrantIdentities;
using FoundryGate.Cli.Commands.Db.SeedReference;
using FoundryGate.Cli.Commands.Db.SeedTest;

namespace FoundryGate.Cli.Commands.Db;

/// <summary>Parent command grouping the FoundryGate database lifecycle subcommands.</summary>
internal sealed class DbCommand : Command
{
    public DbCommand() : base("db", "Commands to manage the FoundryGate database")
    {
        Add(new CompareCommand());
        Add(new DeployCommand());
        Add(new SeedReferenceCommand());
        Add(new SeedTestCommand());
        Add(new GrantIdentitiesCommand());
    }
}
