using FoundryGate.Core.Quota;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Functions.Services.Quota;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Functions.Services.Quota;

/// <summary>
/// <see cref="MonthlyResetJob"/> — the daily tick behind <c>MonthlyQuotaResetFunction</c> (#38): the
/// <c>ResetDayOfMonth</c> gate (#165) and the cross-replica lock. What a reset actually writes is
/// <c>QuotaResetServiceTests</c>' business.
/// </summary>
public class MonthlyResetJobTests : InMemoryDatabaseTest
{
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 0, 1, 0, TimeSpan.Zero));
    private readonly RecordingGatewayTierSync _tierSync = new();

    [Fact]
    public async Task On_the_configured_day_it_takes_the_lock_runs_the_reset_and_releases()
    {
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        var resetLock = new FakeResetLock();

        var outcome = await CreateJob(resetLock).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(MonthlyResetSkipReasonType.None, outcome.SkipReasonType);
        Assert.Equal(1, outcome.Reset!.Value.UsersResetCount);
        Assert.Equal([MonthlyResetJob.LockName], resetLock.Requested);
        Assert.Equal(1, resetLock.Released);
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync());
        Assert.Equal(1, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.QuotaMonthlyReset));
    }

    [Fact]
    public async Task On_any_other_day_it_does_nothing_at_all_and_never_reaches_the_lock()
    {
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 17, 0, 1, 0, TimeSpan.Zero));
        var resetLock = new FakeResetLock();

        var outcome = await CreateJob(resetLock).RunAsync(CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.Equal(MonthlyResetSkipReasonType.NotTheConfiguredDay, outcome.SkipReasonType);
        Assert.Equal(17, outcome.DayOfMonth);
        Assert.Equal(1, outcome.ConfiguredDayOfMonth);
        Assert.Empty(resetLock.Requested);
        Assert.Empty(await Context.QuotaAllocations.AsNoTracking().ToListAsync());
        Assert.Empty(await Context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Changing_ResetDayOfMonth_moves_the_reset_without_a_redeploy()
    {
        // The point of #165: the key was seeded, validated and listed, and read by nothing.
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        await SetResetDayAsync("17");
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 17, 0, 1, 0, TimeSpan.Zero));

        var outcome = await CreateJob(new FakeResetLock()).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(17, outcome.ConfiguredDayOfMonth);
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("31")]
    [InlineData("not-a-day")]
    [InlineData("")]
    public async Task An_unusable_ResetDayOfMonth_falls_back_to_the_first_rather_than_never_resetting(string value)
    {
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        await SetResetDayAsync(value);

        var outcome = await CreateJob(new FakeResetLock()).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(MonthlyResetJob.DefaultResetDayOfMonth, outcome.ConfiguredDayOfMonth);
    }

    [Fact]
    public async Task A_missing_ResetDayOfMonth_row_still_resets_on_the_first()
    {
        // No reference-data seed at all — an unseeded fork must not silently stop resetting.
        await SeedUserAsync("Ada");
        await SeedDefaultQuotaAsync();

        var outcome = await CreateJob(new FakeResetLock()).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(MonthlyResetJob.DefaultResetDayOfMonth, outcome.ConfiguredDayOfMonth);
    }

    [Fact]
    public async Task When_another_replica_holds_the_lock_it_writes_nothing()
    {
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        var resetLock = new FakeResetLock(acquire: false);

        var outcome = await CreateJob(resetLock).RunAsync(CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.Equal(MonthlyResetSkipReasonType.LockHeldElsewhere, outcome.SkipReasonType);
        Assert.Equal(0, resetLock.Released); // nothing was acquired, so nothing to release
        Assert.Empty(await Context.QuotaAllocations.AsNoTracking().ToListAsync());
        Assert.Empty(await Context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_scheduled_reset_never_asks_the_gateway_to_move_a_subscription()
    {
        // Why FunctionsServiceCollectionExtensions registers NullGatewayTierSync unconditionally.
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada", u => u.ApimSubscriptionId = "foundrygate-1");

        _ = await CreateJob(new FakeResetLock()).RunAsync(CancellationToken.None);

        // A user with no prior allocation has an unknown previous tier, so resolution does ask once —
        // which is exactly the case the null sync is safe for: the tier product is already correct,
        // because the subscription was created under it. Any *change* would be a bug.
        Assert.All(_tierSync.Calls, call => Assert.Equal(GatewayTiers.Standard, call.TierProductId));
    }

    private MonthlyResetJob CreateJob(FakeResetLock resetLock)
    {
        var resolution = new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance);
        var reset = new QuotaResetService(Context, resolution, new AuditWriter(Context, _clock), _clock, NullLogger<QuotaResetService>.Instance);

        return new MonthlyResetJob(Context, reset, resetLock, _clock, NullLogger<MonthlyResetJob>.Instance);
    }

    private async Task SetResetDayAsync(string value)
    {
        var row = await Context.SystemConfigurations.SingleAsync(c => c.Key == SystemConfigurationKeys.ResetDayOfMonth);
        row.Value = value;
        await Context.SaveChangesAsync();
    }

    /// <summary>The one configuration row resolution itself needs, for the tests that skip the full seed.</summary>
    private async Task SeedDefaultQuotaAsync()
    {
        Context.SystemConfigurations.Add(new SystemConfiguration
        {
            Key = SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            Value = TestGatewayTiers.StandardCap.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        await Context.SaveChangesAsync();
    }

    private async Task<User> SeedUserAsync(string displayName, Action<User>? configure = null)
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Email = $"{Guid.NewGuid():N}@contoso.test",
        };
        configure?.Invoke(user);
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        return user;
    }
}
