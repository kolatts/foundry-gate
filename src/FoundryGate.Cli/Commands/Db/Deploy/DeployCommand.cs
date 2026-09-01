using System.CommandLine;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace FoundryGate.Cli.Commands.Db.Deploy;

/// <summary>
/// Deploys a built dacpac (<c>dotnet build src/FoundryGate.Database</c>) to a target SQL Server
/// database via DacFx <see cref="DacServices"/>. Works against both SQL auth (connection string
/// carries <c>User Id</c>/<c>Password</c>, e.g. local docker SQL) and Entra auth (connection string
/// carries <c>Authentication=Active Directory Default</c>, per CONVENTIONS.md) — <see cref="DacServices"/>
/// reads the auth method straight out of the connection string, so no separate credential plumbing
/// is needed here (unlike imagile-app's metabase/tenant split, which explicitly branches on SQL vs.
/// Entra auth to build a <c>DacServicesAuthProvider</c>).
/// </summary>
internal sealed class DeployCommand : Command
{
    public DeployCommand() : base("deploy", "Deploys a dacpac to a target SQL Server database")
    {
        var dacpacArg = new Argument<string>("dacpac")
        {
            Description = "Path to the built dacpac file"
        };

        var connectionStringArg = new Argument<string>("connection-string")
        {
            Description = "Connection string to the target database (must include Initial Catalog)"
        };

        var dropObjectsOption = new Option<bool>("--drop-objects", "-d")
        {
            Description = "Drop objects in the target database that are not present in the dacpac"
        };

        var blockOnDataLossOption = new Option<bool>("--block-on-data-loss", "-b")
        {
            Description = "Block the deployment if it could cause data loss"
        };

        Add(dacpacArg);
        Add(connectionStringArg);
        Add(dropObjectsOption);
        Add(blockOnDataLossOption);

        SetAction(context =>
        {
            var dacpacPath = context.GetValue(dacpacArg)!;
            var connectionString = context.GetValue(connectionStringArg)!;
            var dropObjects = context.GetValue(dropObjectsOption);
            var blockOnDataLoss = context.GetValue(blockOnDataLossOption);

            Execute(dacpacPath, connectionString, dropObjects, blockOnDataLoss);
        });
    }

    private static void Execute(string dacpacPath, string connectionString, bool dropObjects, bool blockOnDataLoss)
    {
        if (!File.Exists(dacpacPath))
        {
            throw new FileNotFoundException($"Dacpac file not found: {dacpacPath}", dacpacPath);
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "Connection string must specify an Initial Catalog/Database to deploy to.");
        }

        Console.WriteLine($"Deploying {Path.GetFileName(dacpacPath)} to {builder.DataSource}/{databaseName}...");
        Console.WriteLine($"  --drop-objects: {dropObjects}, --block-on-data-loss: {blockOnDataLoss}");

        var dacServices = new DacServices(connectionString);
        dacServices.ProgressChanged += (_, args) => Console.WriteLine(args.Message);
        dacServices.Message += (_, args) =>
        {
            if (args.Message.MessageType == DacMessageType.Error)
            {
                Console.Error.WriteLine(args.Message.Message);
            }
            else
            {
                Console.WriteLine(args.Message.Message);
            }
        };

        var options = new DacDeployOptions
        {
            BlockOnPossibleDataLoss = blockOnDataLoss,
            GenerateSmartDefaults = true,
            DropObjectsNotInSource = dropObjects,
            ExcludeObjectTypes = [ObjectType.Users],
            ScriptDatabaseCompatibility = true
        };

        using var package = DacPackage.Load(dacpacPath);
        dacServices.Deploy(package, databaseName, upgradeExisting: true, options);

        Console.WriteLine($"Deployed {databaseName} successfully.");
    }
}
