using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Interceptors;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data;

/// <summary>Behavior tests for <see cref="TimestampInterceptor"/> — the mandated CreatedDate/ModifiedDate mechanism.</summary>
public class TimestampInterceptorTests : IDisposable
{
    private static readonly DateTimeOffset InitialNow = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _timeProvider = new(InitialNow);
    private readonly AppDbContext _context;

    public TimestampInterceptorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared")
            .AddInterceptors(new TimestampInterceptor(_timeProvider))
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

        Assert.Equal(InitialNow, user.CreatedDate);
    }

    [Fact]
    public async Task SaveChanges_advances_ModifiedDate_on_update_but_leaves_insert_time_CreatedDate_alone()
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = "Ada Lovelace",
            Email = "ada@contoso.com"
        };
        var config = new SystemConfiguration { Key = "TestKey", Value = "1" };

        _context.Users.Add(user);
        _context.SystemConfigurations.Add(config);
        await _context.SaveChangesAsync();

        var insertTime = InitialNow;
        Assert.Equal(insertTime, user.CreatedDate);
        Assert.Equal(insertTime, config.UpdatedDate);

        // Move the fake clock forward between saves so this test can actually fail if the
        // interceptor stops firing on Modified — asserting against a clock that never advances
        // passes whether or not SavingChanges ran on the update.
        var updateTime = insertTime + TimeSpan.FromHours(3);
        _timeProvider.SetUtcNow(updateTime);

        config.Value = "2";
        user.DisplayName = "Ada King";
        await _context.SaveChangesAsync();

        Assert.Equal(updateTime, config.UpdatedDate);
        Assert.Equal(insertTime, user.CreatedDate); // CreatedDate is insert-only; an update must not touch it
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
