using FoundryGate.Api.Services.Quota;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Imagile.Framework.Configuration.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Api.Services.Quota;

/// <summary>
/// The five-level precedence chain (#32), the upsert contract (preserve <c>TokensUsed</c>/<c>IsHardStopped</c>,
/// never save), the tier mapping on the way out, and the <see cref="IGatewayTierSync"/> seam's
/// invocation rule — on a real SQLite <c>AppDbContext</c> with the real reference-data seed.
/// </summary>
public class QuotaResolutionServiceTests : InMemoryDatabaseTest
{
    private static readonly BillingPeriod Period = new(2026, 9);

    private readonly RecordingGatewayTierSync _tierSync = new();

    // -- The five levels --

    [Fact]
    public async Task Level1_user_unlimited_wins_over_everything_including_a_user_number_and_groups()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u =>
        {
            u.IsUnlimited = true;
            u.MonthlyTokenQuota = 1_000_000;
        });
        await AddToGroupAsync(user, quota: 2_000_000);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.UserUnlimited, result.LevelType);
        Assert.Null(result.AllocatedTokens);
        Assert.Equal(GatewayTiers.Unlimited, result.TierProductId);
        Assert.False(result.IsGatewayCapped);
    }

    [Fact]
    public async Task Level2_user_override_beats_an_unlimited_group_user_level_settings_win_over_group_level()
    {
        // Pinned on purpose: a finite number pinned on the user by an admin means that number, even
        // when one of their groups would grant unlimited.
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = 3_000_000);
        await AddToGroupAsync(user, isUnlimited: true);
        await AddToGroupAsync(user, quota: 50_000_000);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.UserOverride, result.LevelType);
        Assert.Equal(3_000_000, result.AllocatedTokens);
        Assert.Equal(GatewayTiers.Standard, result.TierProductId);
    }

    [Fact]
    public async Task Level3_any_unlimited_group_beats_finite_group_quotas()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync();
        await AddToGroupAsync(user, quota: 7_000_000);
        await AddToGroupAsync(user, isUnlimited: true);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.GroupUnlimited, result.LevelType);
        Assert.Null(result.AllocatedTokens);
        Assert.Equal(GatewayTiers.Unlimited, result.TierProductId);
    }

    [Fact]
    public async Task Level4_takes_the_maximum_group_quota_ignoring_groups_without_one()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync();
        await AddToGroupAsync(user, quota: 2_000_000);
        await AddToGroupAsync(user, quota: 7_000_000);
        await AddToGroupAsync(user, quota: null); // a group with no quota policy contributes nothing

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.GroupMax, result.LevelType);
        Assert.Equal(7_000_000, result.AllocatedTokens);
        Assert.Equal(GatewayTiers.Power, result.TierProductId);
        Assert.False(result.IsGatewayCapped);
    }

    [Fact]
    public async Task Level5_system_default_applies_when_neither_user_nor_groups_say_anything()
    {
        await SeedReferenceDataAsync(); // DefaultMonthlyTokenQuota = "1000000"
        var user = await SeedUserAsync();
        await AddToGroupAsync(user, quota: null);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.SystemDefault, result.LevelType);
        Assert.Equal(1_000_000, result.AllocatedTokens);
        Assert.Equal(GatewayTiers.Standard, result.TierProductId);
    }

    [Fact]
    public async Task System_default_row_missing_is_a_configuration_fault_not_a_zero_quota()
    {
        // No SeedReferenceDataAsync → no SystemConfiguration rows at all.
        var user = await SeedUserAsync();

        var exception = await Assert.ThrowsAsync<ConfigurationValidationException>(() =>
            CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None));

        Assert.Contains(SystemConfigurationKeys.DefaultMonthlyTokenQuota, exception.Message, StringComparison.Ordinal);
        Assert.Contains("seed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("1,000,000")]
    [InlineData("-5")]
    public async Task System_default_that_is_not_a_non_negative_integer_is_a_configuration_fault(string value)
    {
        await SeedReferenceDataAsync();
        await SetSystemDefaultAsync(value);
        var user = await SeedUserAsync();

        var exception = await Assert.ThrowsAsync<ConfigurationValidationException>(() =>
            CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None));

        Assert.Contains($"'{value}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/config", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_default_tolerates_surrounding_whitespace()
    {
        await SeedReferenceDataAsync();
        await SetSystemDefaultAsync("  250000 ");
        var user = await SeedUserAsync();

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(250_000, result.AllocatedTokens);
    }

    [Fact]
    public async Task Unknown_user_throws_KeyNotFoundException()
    {
        await SeedReferenceDataAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateService().ResolveAsync(999_999, Period, CancellationToken.None));
    }

    // -- Tier mapping on the way out --

    [Theory]
    [InlineData(TestGatewayTiers.StandardCap, GatewayTiers.Standard, false)]
    [InlineData(TestGatewayTiers.StandardCap + 1, GatewayTiers.Power, false)]
    [InlineData(TestGatewayTiers.PowerCap, GatewayTiers.Power, false)]
    [InlineData(TestGatewayTiers.PowerCap + 1, GatewayTiers.Power, true)]
    public async Task Resolved_quota_is_mapped_to_a_tier_and_flagged_when_above_every_finite_cap(long quota, string expectedTier, bool expectedCapped)
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = quota);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(expectedTier, result.Allocation.TierProductId);
        Assert.Equal(expectedCapped, result.Allocation.IsGatewayCapped);
        Assert.Equal(quota, result.Allocation.AllocatedTokens); // the numeric quota is still recorded
    }

    // -- Upsert contract --

    [Fact]
    public async Task New_allocation_is_added_with_TokensUsed_zero_and_no_ResetDate_and_is_not_saved()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = 100);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.True(result.IsNew);
        Assert.Null(result.PreviousTierProductId);
        Assert.Equal(0, result.Allocation.TokensUsed);
        Assert.False(result.Allocation.IsHardStopped);
        Assert.Null(result.Allocation.ResetDate);
        Assert.Equal((Period.Year, Period.Month), (result.Allocation.PeriodYear, result.Allocation.PeriodMonth));
        Assert.Equal(EntityState.Added, Context.Entry(result.Allocation).State);
        Assert.Equal(0, await Context.QuotaAllocations.AsNoTracking().CountAsync()); // nothing hit the database

        await Context.SaveChangesAsync();
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync(a => a.UserId == user.UserId));
    }

    [Fact]
    public async Task Existing_allocation_is_updated_in_place_preserving_TokensUsed_IsHardStopped_and_ResetDate()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = 1_000_000);
        var resetDate = new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero);
        var existing = await SeedAllocationAsync(user, Period, allocated: 1_000_000, tokensUsed: 123_456, isHardStopped: true, tier: GatewayTiers.Standard, resetDate: resetDate);

        user.MonthlyTokenQuota = 9_000_000; // admin raised the quota mid-month
        await Context.SaveChangesAsync();

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.False(result.IsNew);
        Assert.Equal(existing.QuotaAllocationId, result.Allocation.QuotaAllocationId);
        var saved = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == user.UserId);
        Assert.Equal(9_000_000, saved.AllocatedTokens);
        Assert.Equal(QuotaLevelType.UserOverride, saved.ResolvedLevelType);
        Assert.Equal(GatewayTiers.Power, saved.TierProductId);
        Assert.Equal(123_456, saved.TokensUsed); // untouched
        Assert.True(saved.IsHardStopped); // untouched
        Assert.Equal(resetDate, saved.ResetDate); // untouched
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync(a => a.UserId == user.UserId)); // no duplicate
    }

    [Fact]
    public async Task Resolving_twice_in_one_unit_of_work_reuses_the_unsaved_row_instead_of_adding_a_duplicate()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = 100);
        var service = CreateService();

        var first = await service.ResolveAsync(user.UserId, Period, CancellationToken.None);
        var second = await service.ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Same(first.Allocation, second.Allocation);
        await Context.SaveChangesAsync(); // would throw on the unique index if two rows had been added
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync(a => a.UserId == user.UserId));
    }

    // -- IGatewayTierSync seam --

    [Fact]
    public async Task Tier_sync_is_not_invoked_for_a_user_without_an_APIM_subscription()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = 100);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.False(result.TierSyncRequested);
        Assert.Empty(_tierSync.Calls);
    }

    [Fact]
    public async Task Tier_sync_is_invoked_when_a_subscribed_user_has_no_earlier_allocation_previous_tier_unknown()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = 100;
            u.ApimSubscriptionId = "sub-1";
        });

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.True(result.TierSyncRequested);
        Assert.True(result.TierChanged);
        Assert.Equal([(user.UserId, GatewayTiers.Standard)], _tierSync.Calls);
    }

    [Fact]
    public async Task Tier_sync_is_not_invoked_when_the_existing_allocation_is_already_on_the_resolved_tier()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = 100;
            u.ApimSubscriptionId = "sub-1";
        });
        await SeedAllocationAsync(user, Period, allocated: 50, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Standard);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(GatewayTiers.Standard, result.PreviousTierProductId);
        Assert.False(result.TierChanged);
        Assert.False(result.TierSyncRequested);
        Assert.Empty(_tierSync.Calls);
    }

    [Fact]
    public async Task Tier_sync_is_invoked_when_the_resolved_tier_differs_from_the_existing_allocations()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap + 1;
            u.ApimSubscriptionId = "sub-1";
        });
        await SeedAllocationAsync(user, Period, allocated: 50, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Standard);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(GatewayTiers.Standard, result.PreviousTierProductId);
        Assert.Equal(GatewayTiers.Power, result.TierProductId);
        Assert.True(result.TierSyncRequested);
        Assert.Equal([(user.UserId, GatewayTiers.Power)], _tierSync.Calls);
    }

    [Fact]
    public async Task Previous_tier_for_a_new_period_comes_from_the_most_recent_earlier_allocation()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = 100;
            u.ApimSubscriptionId = "sub-1";
        });
        await SeedAllocationAsync(user, new BillingPeriod(2026, 7), allocated: 100, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Power);
        await SeedAllocationAsync(user, new BillingPeriod(2026, 8), allocated: 100, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Standard);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.True(result.IsNew);
        Assert.Equal(GatewayTiers.Standard, result.PreviousTierProductId); // August, not July
        Assert.False(result.TierSyncRequested); // still Standard → nothing to move
        Assert.Empty(_tierSync.Calls);
    }

    // -- ResolveManyAsync --

    [Fact]
    public async Task ResolveManyAsync_resolves_every_known_user_in_order_skipping_unknown_ids_and_reusing_existing_rows()
    {
        await SeedReferenceDataAsync();
        var alice = await SeedUserAsync(u => u.MonthlyTokenQuota = 100);
        var bob = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
            u.ApimSubscriptionId = "sub-bob";
        });
        var carol = await SeedUserAsync(u => u.IsUnlimited = true);
        await AddToGroupAsync(alice, isUnlimited: true); // ignored: alice has a user override
        await SeedAllocationAsync(bob, new BillingPeriod(2026, 8), allocated: 100, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Standard);
        var carolExisting = await SeedAllocationAsync(carol, Period, allocated: 1, tokensUsed: 42, isHardStopped: true, tier: GatewayTiers.Standard);

        var results = await CreateService().ResolveManyAsync([carol.UserId, 999_999, alice.UserId, bob.UserId, alice.UserId], Period, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.Equal([carol.UserId, alice.UserId, bob.UserId], results.Select(r => r.Allocation.UserId));

        var carolResult = results[0];
        Assert.False(carolResult.IsNew);
        Assert.Equal(carolExisting.QuotaAllocationId, carolResult.Allocation.QuotaAllocationId);
        Assert.Equal(QuotaLevelType.UserUnlimited, carolResult.LevelType);
        Assert.Equal(42, carolResult.Allocation.TokensUsed);
        Assert.True(carolResult.Allocation.IsHardStopped);

        var aliceResult = results[1];
        Assert.True(aliceResult.IsNew);
        Assert.Equal(QuotaLevelType.UserOverride, aliceResult.LevelType);
        Assert.False(aliceResult.TierSyncRequested); // no subscription

        var bobResult = results[2];
        Assert.True(bobResult.IsNew);
        Assert.Equal(GatewayTiers.Standard, bobResult.PreviousTierProductId); // from August
        Assert.Equal(GatewayTiers.Power, bobResult.TierProductId);
        Assert.True(bobResult.TierSyncRequested);
        Assert.Equal([(bob.UserId, GatewayTiers.Power)], _tierSync.Calls);

        Assert.Equal(3, await Context.QuotaAllocations.AsNoTracking().CountAsync(a => a.PeriodYear == Period.Year && a.PeriodMonth == Period.Month));
    }

    [Fact]
    public async Task ResolveManyAsync_with_no_ids_returns_empty_without_touching_the_database()
    {
        var results = await CreateService().ResolveManyAsync([], Period, CancellationToken.None);

        Assert.Empty(results);
    }

    // -- Helpers --

    private QuotaResolutionService CreateService() =>
        new(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance);

    private async Task<User> SeedUserAsync(Action<User>? configure = null)
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = "Dev",
            Email = $"{Guid.NewGuid():N}@contoso.test",
        };
        configure?.Invoke(user);
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        return user;
    }

    private async Task AddToGroupAsync(User user, long? quota = null, bool isUnlimited = false)
    {
        var group = new Group { Name = $"g-{Guid.NewGuid():N}", MonthlyTokenQuota = quota, IsUnlimited = isUnlimited };
        Context.Groups.Add(group);
        await Context.SaveChangesAsync();
        Context.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = user.UserId });
        await Context.SaveChangesAsync();
    }

    private async Task<QuotaAllocation> SeedAllocationAsync(User user, BillingPeriod period, long? allocated, long tokensUsed, bool isHardStopped, string tier, DateTimeOffset? resetDate = null)
    {
        var allocation = new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            AllocatedTokens = allocated,
            TokensUsed = tokensUsed,
            IsHardStopped = isHardStopped,
            ResolvedLevelType = QuotaLevelType.UserOverride,
            TierProductId = tier,
            ResetDate = resetDate,
        };
        Context.QuotaAllocations.Add(allocation);
        await Context.SaveChangesAsync();
        Context.Entry(allocation).State = EntityState.Detached; // the service must find it in the database, not the tracker
        return allocation;
    }

    private async Task SetSystemDefaultAsync(string value)
    {
        var row = await Context.SystemConfigurations.SingleAsync(c => c.Key == SystemConfigurationKeys.DefaultMonthlyTokenQuota);
        row.Value = value;
        await Context.SaveChangesAsync();
    }
}
