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
    private static readonly TimeSpan DeployTimeout = TimeSpan.FromMinutes(10);

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

        // Safety is the default (DacFx's own BlockOnPossibleDataLoss=true) — CONVENTIONS.md
        // deliberately deviates from imagile-app here (which inverts this to opt-in blocking): a
        // fork's production database deserves a safe default more than CI convenience does. Pass
        // --allow-data-loss to explicitly opt out.
        var allowDataLossOption = new Option<bool>("--allow-data-loss")
        {
            Description = "Allow the deployment to proceed even if it could cause data loss (default: blocked)"
        };

        Add(dacpacArg);
        Add(connectionStringArg);
        Add(dropObjectsOption);
        Add(allowDataLossOption);

        SetAction(async (context, cancellationToken) =>
        {
            var dacpacPath = context.GetValue(dacpacArg)!;
            var connectionString = context.GetValue(connectionStringArg)!;
            var dropObjects = context.GetValue(dropObjectsOption);
            var allowDataLoss = context.GetValue(allowDataLossOption);

            await ExecuteAsync(dacpacPath, connectionString, dropObjects, allowDataLoss, cancellationToken);
        });
    }

    private static async Task ExecuteAsync(
        string dacpacPath,
        string connectionString,
        bool dropObjects,
        bool allowDataLoss,
        CancellationToken cancellationToken)
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
        Console.WriteLine($"  --drop-objects: {dropObjects}, --allow-data-loss: {allowDataLoss} (BlockOnPossibleDataLoss: {!allowDataLoss})");

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
            BlockOnPossibleDataLoss = !allowDataLoss,
            GenerateSmartDefaults = true,
            DropObjectsNotInSource = dropObjects,
            ExcludeObjectTypes = [ObjectType.Users],
            ScriptDatabaseCompatibility = true
        };

        // Same 10-minute deploy ceiling imagile-app's DeployCommand uses, linked to the process's
        // own cancellation (Ctrl+C) so either one aborts the blocking DacServices.Deploy call.
        using var timeoutSource = new CancellationTokenSource(DeployTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        using var package = DacPackage.Load(dacpacPath);
        await Task.Run(
            () => dacServices.Deploy(package, databaseName, upgradeExisting: true, options, linkedSource.Token),
            linkedSource.Token);

        Console.WriteLine($"Deployed {databaseName} successfully.");
    }
}
