using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data;

/// <summary>Behavior tests for <see cref="TimestampInterceptor"/> — the mandated CreatedDate/ModifiedDate mechanism.</summary>
public class TimestampInterceptorTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _context;

    public TimestampInterceptorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared")
            .AddInterceptors(new TimestampInterceptor(new FixedTimeProvider(FixedNow)))
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();
    }

    [Fact]
    public async Task SaveChanges_sets_CreatedDate_on_insert_via_TimeProvider()
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = "Ada Lovelace",
            Email = "ada@contoso.com"
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        Assert.Equal(FixedNow, user.CreatedDate);
    }

    [Fact]
    public async Task SaveChanges_sets_ModifiedDate_on_insert_and_update_via_TimeProvider()
    {
        var config = new SystemConfiguration { Key = "TestKey", Value = "1" };
        _context.SystemConfigurations.Add(config);
        await _context.SaveChangesAsync();
        Assert.Equal(FixedNow, config.ModifiedDate);

        config.Value = "2";
        await _context.SaveChangesAsync();
        Assert.Equal(FixedNow, config.ModifiedDate);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
