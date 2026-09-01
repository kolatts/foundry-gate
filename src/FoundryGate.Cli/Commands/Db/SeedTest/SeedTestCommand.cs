using System.CommandLine;
using FoundryGate.Cli.Helpers;
using FoundryGate.Data.Seeding;

namespace FoundryGate.Cli.Commands.Db.SeedTest;

/// <summary>
/// Seeds Bogus-generated demo data (<see cref="TestDataSeeder"/>) to the target database.
/// Local/dev/CI only — never run against a production connection string. No-ops if any
/// <c>User</c> row already exists, so it is safe to call repeatedly without piling up duplicates.
/// </summary>
internal sealed class SeedTestCommand : Command
{
    public SeedTestCommand() : base("seed-test", "Seeds Bogus-generated demo data to the target database (local/dev/CI only)")
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

        Console.WriteLine("Seeding demo test data...");
        await TestDataSeeder.SeedAsync(context, TimeProvider.System, cancellationToken: cancellationToken);

        Console.WriteLine("Test data seeding complete.");
    }
}
