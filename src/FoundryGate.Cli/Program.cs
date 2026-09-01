using System.CommandLine;

var rootCommand = new RootCommand("FoundryGate CLI");

return await rootCommand.Parse(args).InvokeAsync();
