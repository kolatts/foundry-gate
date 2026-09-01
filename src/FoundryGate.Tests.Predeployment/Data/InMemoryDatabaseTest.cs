using FoundryGate.Data;
using FoundryGate.Data.Seeding;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data;

/// <summary>
/// Base class for tests that need a real (SQLite in-memory) <see cref="AppDbContext"/>. Mirrors
/// imagile-app's <c>InMemoryDatabaseTest</c> harness, minus the metabase/tenant split — Foundry
/// Gate has one context.
/// </summary>
public abstract class InMemoryDatabaseTest : IDisposable
{
    protected InMemoryDatabaseTest()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared")
            .Options;

        Context = new AppDbContext(options);
        Context.Database.EnsureCreated();
    }

    protected AppDbContext Context { get; }

    /// <summary>Seeds the code-defined reference data (currently just <c>SystemConfiguration</c>).</summary>
    protected Task<Dictionary<string, ReferenceDataSyncResult>> SeedReferenceDataAsync() =>
        ReferenceDataSeeder.SeedAsync(Context);

    /// <summary>Seeds Bogus-generated demo data.</summary>
    protected Task SeedTestDataAsync(int developerCount = 8) =>
        TestDataSeeder.SeedAsync(Context, TimeProvider.System, developerCount);

    public void Dispose()
    {
        Context.Database.EnsureDeleted();
        Context.Dispose();
        GC.SuppressFinalize(this);
    }
}
