using System.CommandLine;
using FoundryGate.Cli.Helpers;
using FoundryGate.Data.Seeding;

namespace FoundryGate.Cli.Commands.Db.SeedReference;

/// <summary>
/// Syncs the code-defined reference data (currently <c>SystemConfiguration</c>'s eight placeholder
/// keys, via <see cref="ReferenceDataSeeder"/>) to the target database. Idempotent and safe to run
/// on every deploy — see <c>ReferenceDataExtensions.SyncReferenceDataAsync</c> for the
/// <c>[DoNotUpdate]</c>-respecting upsert semantics that keep it from clobbering operator edits.
/// </summary>
internal sealed class SeedReferenceCommand : Command
{
    public SeedReferenceCommand() : base("seed-reference", "Syncs code-defined reference data to the target database")
    {
        var connectionStringArg = new Argument<string>("connection-string")
        {
            Description = "Connection string to the target database"
        };

        Add(connectionStringArg);

        SetAction(async (context, cancellationToken) =>
        {
            var connectionString = context.GetValue(connectionStringArg)!;
            await ExecuteAsync(connectionString, cancellationToken);
        });
    }

    private static async Task ExecuteAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var context = CliDbContextFactory.Create(connectionString);

        Console.WriteLine("Syncing reference data...");
        var results = await ReferenceDataSeeder.SeedAsync(context, cancellationToken);
        foreach (var (entityName, result) in results)
        {
            Console.WriteLine(result.TotalChanges == 0
                ? $"  {entityName}: no changes"
                : $"  {entityName}: +{result.Added} ~{result.Updated} -{result.Deleted}");
        }

        Console.WriteLine("Reference data sync complete.");
    }
}
