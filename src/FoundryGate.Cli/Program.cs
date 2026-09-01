using System.CommandLine;
using FoundryGate.Cli.Commands.Db;
using FoundryGate.Cli.Commands.Ip;
using FoundryGate.Cli.Commands.Local;

var rootCommand = new RootCommand("FoundryGate CLI");
rootCommand.Add(new DbCommand());
rootCommand.Add(new LocalCommand());
rootCommand.Add(new IpCommand());

return await rootCommand.Parse(args).InvokeAsync();
