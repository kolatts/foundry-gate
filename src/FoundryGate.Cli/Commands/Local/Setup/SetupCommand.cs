using System.CommandLine;
using FoundryGate.Cli.Helpers;
using FoundryGate.Data.Seeding;

namespace FoundryGate.Cli.Commands.Local.Setup;

/// <summary>
/// One-command local dev bootstrap: drops and re-creates the local FoundryGate database straight
/// from the current EF model (<c>EnsureCreated</c> — CONVENTIONS.md's "no EF migrations" schema
/// pipeline), then seeds reference data and, optionally, Bogus demo data.
/// </summary>
internal sealed class SetupCommand : Command
{
    /// <summary>
    /// Matches <c>src/FoundryGate.Api/appsettings.Development.json</c> and the <c>sql-server-db</c>
    /// service in the repo's <c>docker-compose.yml</c> (port 3433, sa/Temp1234!) — the one local SQL
    /// Server every FoundryGate developer/CI docker-compose stack already runs. Uses the literal
    /// <c>127.0.0.1</c> rather than <c>localhost</c>: SqlClient's dual-stack connection attempt can
    /// time out resolving <c>localhost</c> to the container's IPv6 loopback (which docker's port
    /// mapping doesn't listen on) before falling back to IPv4, even though the port is reachable.
    /// </summary>
    internal const string LocalConnectionString =
        "Server=127.0.0.1,3433;Database=FoundryGate;User Id=sa;Password=Temp1234!;TrustServerCertificate=True";

    public SetupCommand() : base("setup", "Creates the local FoundryGate database (drop + EnsureCreated) and seeds it")
    {
        var testDataOption = new Option<bool>("--test-data")
        {
            Description = "Also seed Bogus-generated demo data (local/dev only)"
        };

        Add(testDataOption);

        SetAction(async context =>
        {
            var seedTestData = context.GetValue(testDataOption);
            await ExecuteAsync(seedTestData);
        });
    }

    private static async Task ExecuteAsync(bool seedTestData)
    {
        Console.WriteLine("Connecting to local SQL Server (localhost,3433)...");

        await using var context = CliDbContextFactory.Create(LocalConnectionString);

        Console.WriteLine("Dropping any existing FoundryGate database...");
        await context.Database.EnsureDeletedAsync();

        Console.WriteLine("Creating FoundryGate database from the current EF model...");
        await context.Database.EnsureCreatedAsync();

        Console.WriteLine("Seeding reference data...");
        var referenceResults = await ReferenceDataSeeder.SeedAsync(context);
        foreach (var (entityName, result) in referenceResults)
        {
            Console.WriteLine($"  {entityName}: +{result.Added} ~{result.Updated} -{result.Deleted}");
        }

        if (seedTestData)
        {
            Console.WriteLine("Seeding demo test data...");
            await TestDataSeeder.SeedAsync(context, TimeProvider.System);
        }

        Console.WriteLine("Local setup complete.");
        Console.WriteLine($"Connection string: {LocalConnectionString}");
    }
}
