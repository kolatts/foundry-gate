using FoundryGate.Data;
using FoundryGate.Data.Seeding;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data;

/// <summary>
/// Base class for tests that need a real (SQLite in-memory) <see cref="AppDbContext"/>. Mirrors
/// imagile-app's <c>InMemoryDatabaseTest</c> harness, minus the metabase/tenant split — Foundry
/// Gate has one context.
/// </summary>
public abstract class InMemoryDatabaseTest : IDisposable
{
    private readonly RecordingCommandInterceptor _commands = new();

    protected InMemoryDatabaseTest()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared")
            .AddInterceptors(_commands)
            .Options;

        Context = new AppDbContext(options);
        Context.Database.EnsureCreated();
    }

    protected AppDbContext Context { get; }

    /// <summary>
    /// The SQL of every statement <see cref="Context"/> has executed, in order. For assertions about
    /// query <em>count</em> — "the run reads the whole table once, not once per group" is a claim the
    /// returned rows cannot make on their own.
    /// </summary>
    protected IReadOnlyList<string> ExecutedCommands => _commands.Commands;

    /// <summary>
    /// How many statements executed so far are unfiltered reads of <paramref name="table"/> — the
    /// whole-table snapshot shape, as opposed to a keyed lookup, which always carries a <c>WHERE</c>.
    /// </summary>
    protected int CountWholeTableReads(string table) =>
        ExecutedCommands.Count(sql =>
            sql.Contains($"FROM \"{table}\"", StringComparison.Ordinal)
            && !sql.Contains("WHERE", StringComparison.Ordinal));

    /// <summary>
    /// A second context on the same database, so "nothing was saved" assertions cannot be fooled by
    /// <see cref="Context"/>'s change tracker handing back rows that only exist in memory.
    /// </summary>
    protected AppDbContext CreateVerificationContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Context.Database.GetDbConnection())
            .Options);

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
