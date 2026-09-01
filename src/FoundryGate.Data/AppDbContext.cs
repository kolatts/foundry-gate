using FoundryGate.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FoundryGate.Data;

/// <summary>
/// The single EF Core context for Foundry Gate's control-plane database. Registered plainly via
/// <c>AddDbContext&lt;AppDbContext&gt;</c> (see <see cref="ServiceCollectionExtensions"/>) — no
/// sharding, no per-tenant context resolution; a fork IS the tenant (CONVENTIONS.md).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<QuotaAllocation> QuotaAllocations => Set<QuotaAllocation>();

    public DbSet<QuotaIncreaseRequest> QuotaIncreaseRequests => Set<QuotaIncreaseRequest>();

    public DbSet<SystemConfiguration> SystemConfigurations => Set<SystemConfiguration>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Provider-name check rather than Database.IsSqlite(): that extension lives in the SQLite
        // provider package, which only the test project references (Data ships SQL Server only).
        if (Database.ProviderName == SqliteProviderName)
        {
            ApplySqliteDateTimeOffsetConversion(modelBuilder);
        }
    }

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>
    /// SQLite-only (i.e. test-harness-only) workaround, straight from the EF Core "SQLite
    /// limitations" docs: SQLite has no native <see cref="DateTimeOffset"/>, and the provider refuses
    /// to translate ordering or comparison on the TEXT it would otherwise store ("SQLite cannot order
    /// by expressions of type 'DateTimeOffset'"). Storing every <see cref="DateTimeOffset"/> column as
    /// its <see cref="DateTimeOffset.UtcTicks"/> instead makes <c>OrderBy</c>/<c>&gt;=</c> filters
    /// (the audit log's date range, quota periods, review dates) translate and behave exactly as on
    /// SQL Server. Normalizing to UTC ticks, rather than EF's built-in
    /// <c>DateTimeOffsetToBinaryConverter</c> (which packs the offset into the low bits), means a
    /// query parameter carrying a non-UTC offset still compares correctly against the UTC values the
    /// interceptor writes. Never runs under SQL Server, so the production model and the
    /// <c>FoundryGate.Database</c> dacpac are untouched.
    /// </summary>
    /// <remarks>
    /// Known, accepted test-only divergence: SQL Server's <c>datetimeoffset</c> preserves the offset
    /// a value was written with (<c>-05:00</c> reads back as <c>-05:00</c>); this converter reads
    /// everything back as <c>+00:00</c>. The instant is identical, so <c>==</c>/ordering assertions
    /// hold, but an assertion on <c>.Offset</c> or on <c>ToString()</c> of a non-UTC value would pass
    /// on SQL Server and fail here. Application code only ever writes UTC (interceptor +
    /// <see cref="TimeProvider"/>), so nothing in production depends on the offset surviving.
    /// </remarks>
    private static void ApplySqliteDateTimeOffsetConversion(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(converter);
                }
            }
        }
    }
}
