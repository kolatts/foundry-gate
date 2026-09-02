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
/// never save), the tier mapping on the way out (a budget is a tier — D-013), and the
/// <see cref="IGatewayTierSync"/> seam's invocation rule — on a real SQLite <c>AppDbContext</c> with the
/// real reference-data seed.
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
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
        });
        await AddToGroupAsync(user, quota: TestGatewayTiers.PowerCap);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.UserUnlimited, result.Allocation.ResolvedLevelType);
        Assert.Null(result.Allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Unlimited, result.Allocation.TierProductId);
        Assert.False(result.Allocation.IsGatewayCapped);
    }

    [Fact]
    public async Task Level2_user_override_beats_an_unlimited_group_user_level_settings_win_over_group_level()
    {
        // Pinned on purpose: a finite tier pinned on the user by an admin means that tier, even when
        // one of their groups would grant unlimited.
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        await AddToGroupAsync(user, isUnlimited: true);
        await AddToGroupAsync(user, quota: TestGatewayTiers.PowerCap);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.UserOverride, result.Allocation.ResolvedLevelType);
        Assert.Equal(TestGatewayTiers.StandardCap, result.Allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Standard, result.Allocation.TierProductId);
        Assert.False(result.Allocation.IsGatewayCapped);
    }

    [Fact]
    public async Task Level3_any_unlimited_group_beats_finite_group_quotas()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync();
        await AddToGroupAsync(user, quota: TestGatewayTiers.PowerCap);
        await AddToGroupAsync(user, isUnlimited: true);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.GroupUnlimited, result.Allocation.ResolvedLevelType);
        Assert.Null(result.Allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Unlimited, result.Allocation.TierProductId);
    }

    [Fact]
    public async Task Level4_takes_the_maximum_group_quota_ignoring_groups_without_one()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync();
        await AddToGroupAsync(user, quota: TestGatewayTiers.StandardCap);
        await AddToGroupAsync(user, quota: TestGatewayTiers.PowerCap);
        await AddToGroupAsync(user, quota: null); // a group with no quota policy contributes nothing

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.GroupMax, result.Allocation.ResolvedLevelType);
        Assert.Equal(TestGatewayTiers.PowerCap, result.Allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Power, result.Allocation.TierProductId);
        Assert.False(result.Allocation.IsGatewayCapped);
    }

    [Fact]
    public async Task Level5_system_default_applies_when_neither_user_nor_groups_say_anything()
    {
        await SeedReferenceDataAsync(); // DefaultMonthlyTokenQuota = "5000000" = the Standard tier cap
        var user = await SeedUserAsync();
        await AddToGroupAsync(user, quota: null);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.SystemDefault, result.Allocation.ResolvedLevelType);
        Assert.Equal(TestGatewayTiers.StandardCap, result.Allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Standard, result.Allocation.TierProductId);
        Assert.False(result.Allocation.IsGatewayCapped);
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
        await SetSystemDefaultAsync("  20000000 ");
        var user = await SeedUserAsync();

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(TestGatewayTiers.PowerCap, result.Allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Power, result.Allocation.TierProductId);
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
    [InlineData(TestGatewayTiers.PowerCap, GatewayTiers.Power, false)]
    [InlineData(1_000_000, GatewayTiers.Standard, true)] // legacy non-tier value: enforced at the next tier up, flagged
    [InlineData(TestGatewayTiers.StandardCap + 1, GatewayTiers.Power, true)]
    [InlineData(TestGatewayTiers.PowerCap + 1, GatewayTiers.Power, true)]
    public async Task Resolved_quota_is_mapped_to_its_tier_and_a_non_tier_value_is_flagged_never_thrown(long quota, string expectedTier, bool expectedCapped)
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = quota);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(expectedTier, result.Allocation.TierProductId);
        Assert.Equal(expectedCapped, result.Allocation.IsGatewayCapped);
        Assert.Equal(quota, result.Allocation.AllocatedTokens); // the stored number is still recorded as-is
    }

    // -- Upsert contract --

    [Fact]
    public async Task New_allocation_is_added_with_TokensUsed_zero_and_no_ResetDate_and_is_not_saved()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);

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
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var resetDate = new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero);
        var existing = await SeedAllocationAsync(user, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 123_456, isHardStopped: true, tier: GatewayTiers.Standard, resetDate: resetDate);

        user.MonthlyTokenQuota = TestGatewayTiers.PowerCap; // admin moved the user up a tier mid-month
        await Context.SaveChangesAsync();

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.False(result.IsNew);
        Assert.Equal(existing.QuotaAllocationId, result.Allocation.QuotaAllocationId);
        var saved = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == user.UserId);
        Assert.Equal(TestGatewayTiers.PowerCap, saved.AllocatedTokens);
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
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
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
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);

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
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
            u.ApimSubscriptionId = "sub-1";
        });

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.True(result.TierSyncRequested);
        Assert.Null(result.PreviousTierProductId);
        Assert.Equal([(user.UserId, GatewayTiers.Standard)], _tierSync.Calls);
    }

    [Fact]
    public async Task Tier_sync_is_not_invoked_when_the_existing_allocation_is_already_on_the_resolved_tier()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
            u.ApimSubscriptionId = "sub-1";
        });
        await SeedAllocationAsync(user, Period, allocated: 50, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Standard);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(GatewayTiers.Standard, result.PreviousTierProductId);
        Assert.Equal(GatewayTiers.Standard, result.Allocation.TierProductId);
        Assert.False(result.TierSyncRequested);
        Assert.Empty(_tierSync.Calls);
    }

    [Fact]
    public async Task Tier_sync_is_invoked_when_the_resolved_tier_differs_from_the_existing_allocations()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
            u.ApimSubscriptionId = "sub-1";
        });
        await SeedAllocationAsync(user, Period, allocated: 50, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Standard);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(GatewayTiers.Standard, result.PreviousTierProductId);
        Assert.Equal(GatewayTiers.Power, result.Allocation.TierProductId);
        Assert.True(result.TierSyncRequested);
        Assert.Equal([(user.UserId, GatewayTiers.Power)], _tierSync.Calls);
    }

    [Fact]
    public async Task Previous_tier_for_a_new_period_comes_from_the_most_recent_earlier_allocation()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
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
        var alice = await SeedUserAsync(u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var bob = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
            u.ApimSubscriptionId = "sub-bob";
        });
        var carol = await SeedUserAsync(u => u.IsUnlimited = true);
        await AddToGroupAsync(alice, isUnlimited: true); // ignored: alice has a user override
        await SeedAllocationAsync(bob, new BillingPeriod(2026, 7), allocated: 100, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Power);
        await SeedAllocationAsync(bob, new BillingPeriod(2026, 8), allocated: 100, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Standard);
        var carolExisting = await SeedAllocationAsync(carol, Period, allocated: 1, tokensUsed: 42, isHardStopped: true, tier: GatewayTiers.Standard);

        var results = await CreateService().ResolveManyAsync([carol.UserId, 999_999, alice.UserId, bob.UserId, alice.UserId], Period, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.Equal([carol.UserId, alice.UserId, bob.UserId], results.Select(r => r.Allocation.UserId));

        var carolResult = results[0];
        Assert.False(carolResult.IsNew);
        Assert.Equal(carolExisting.QuotaAllocationId, carolResult.Allocation.QuotaAllocationId);
        Assert.Equal(QuotaLevelType.UserUnlimited, carolResult.Allocation.ResolvedLevelType);
        Assert.Equal(42, carolResult.Allocation.TokensUsed);
        Assert.True(carolResult.Allocation.IsHardStopped);

        var aliceResult = results[1];
        Assert.True(aliceResult.IsNew);
        Assert.Equal(QuotaLevelType.UserOverride, aliceResult.Allocation.ResolvedLevelType);
        Assert.False(aliceResult.TierSyncRequested); // no subscription

        var bobResult = results[2];
        Assert.True(bobResult.IsNew);
        Assert.Equal(GatewayTiers.Standard, bobResult.PreviousTierProductId); // August (most recent), not July's Power
        Assert.Equal(GatewayTiers.Power, bobResult.Allocation.TierProductId);
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

    // -- Pending changes: levels 3-4 read group state through the change tracker --

    [Fact]
    public async Task A_group_quota_edited_but_not_yet_saved_is_what_resolution_uses()
    {
        // The sequence GroupService.UpdateAsync performs: edit the tracked Group, then re-resolve, then
        // one SaveChangesAsync. A projection query straight to the database would still see PowerCap.
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync();
        var group = await AddToGroupAsync(user, quota: TestGatewayTiers.PowerCap);

        group.MonthlyTokenQuota = TestGatewayTiers.StandardCap;

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.GroupMax, result.Allocation.ResolvedLevelType);
        Assert.Equal(TestGatewayTiers.StandardCap, result.Allocation.AllocatedTokens);
    }

    [Fact]
    public async Task A_membership_added_but_not_yet_saved_already_counts()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync();
        var group = new Group { Name = $"g-{Guid.NewGuid():N}", MonthlyTokenQuota = TestGatewayTiers.PowerCap };
        Context.Groups.Add(group);
        await Context.SaveChangesAsync();

        Context.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = user.UserId });

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.GroupMax, result.Allocation.ResolvedLevelType);
        Assert.Equal(TestGatewayTiers.PowerCap, result.Allocation.AllocatedTokens);
    }

    [Fact]
    public async Task A_membership_removed_but_not_yet_saved_no_longer_counts()
    {
        await SeedReferenceDataAsync(); // system default = the Standard cap
        var user = await SeedUserAsync();
        _ = await AddToGroupAsync(user, quota: TestGatewayTiers.PowerCap);

        var membership = await Context.GroupMembers.SingleAsync(gm => gm.UserId == user.UserId);
        Context.GroupMembers.Remove(membership);

        var result = await CreateService().ResolveAsync(user.UserId, Period, CancellationToken.None);

        Assert.Equal(QuotaLevelType.SystemDefault, result.Allocation.ResolvedLevelType);
        Assert.Equal(TestGatewayTiers.StandardCap, result.Allocation.AllocatedTokens);
    }

    [Fact]
    public async Task A_group_removed_but_not_yet_saved_takes_its_memberships_with_it()
    {
        // GroupService.DeleteAsync removes the GroupMember rows explicitly, but the relationship also
        // cascades — a caller that only removes the Group must not resolve against its policy either.
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync();
        var group = await AddToGroupAsync(user, quota: TestGatewayTiers.PowerCap);

        Context.Groups.Remove(group);

        var result = await CreateService().ResolveManyAsync([user.UserId], Period, CancellationToken.None);

        var allocation = Assert.Single(result).Allocation;
        Assert.Equal(QuotaLevelType.SystemDefault, allocation.ResolvedLevelType);
        Assert.Equal(TestGatewayTiers.StandardCap, allocation.AllocatedTokens);
    }

    // -- PreviewAsync --

    [Fact]
    public async Task PreviewAsync_answers_the_same_chain_as_ResolveAsync_but_writes_nothing()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
            u.ApimSubscriptionId = "sub-preview"; // ResolveAsync would sync this one
        });

        var preview = await CreateService().PreviewAsync(user.UserId, CancellationToken.None);

        Assert.Equal(TestGatewayTiers.PowerCap, preview.Quota);
        Assert.Equal(QuotaLevelType.UserOverride, preview.Level);
        Assert.False(await Context.QuotaAllocations.AsNoTracking().AnyAsync()); // no upsert
        Assert.Empty(Context.ChangeTracker.Entries<QuotaAllocation>()); // not even tracked
        Assert.Empty(_tierSync.Calls); // and no gateway move
    }

    [Fact]
    public async Task PreviewAsync_walks_every_level_including_group_ones()
    {
        await SeedReferenceDataAsync();
        var unlimitedUser = await SeedUserAsync(u => u.IsUnlimited = true);
        var groupUser = await SeedUserAsync();
        await AddToGroupAsync(groupUser, quota: TestGatewayTiers.PowerCap);
        var defaultUser = await SeedUserAsync();
        var service = CreateService();

        var unlimited = await service.PreviewAsync(unlimitedUser.UserId, CancellationToken.None);
        var group = await service.PreviewAsync(groupUser.UserId, CancellationToken.None);
        var systemDefault = await service.PreviewAsync(defaultUser.UserId, CancellationToken.None);

        Assert.Equal(QuotaLevelType.UserUnlimited, unlimited.Level);
        Assert.Null(unlimited.Quota);
        Assert.Equal(QuotaLevelType.GroupMax, group.Level);
        Assert.Equal(TestGatewayTiers.PowerCap, group.Quota);
        Assert.Equal(QuotaLevelType.SystemDefault, systemDefault.Level);
        Assert.NotNull(systemDefault.Quota);
    }

    [Fact]
    public async Task PreviewAsync_ignores_a_stale_allocation_row_and_reports_the_users_current_settings()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync(u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);
        _ = await SeedAllocationAsync(user, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 0, isHardStopped: false, tier: GatewayTiers.Standard);

        var preview = await CreateService().PreviewAsync(user.UserId, CancellationToken.None);

        Assert.Equal(TestGatewayTiers.PowerCap, preview.Quota);
    }

    [Fact]
    public async Task PreviewAsync_unknown_user_is_404()
    {
        await SeedReferenceDataAsync();

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateService().PreviewAsync(999_999, CancellationToken.None));
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

    private async Task<Group> AddToGroupAsync(User user, long? quota = null, bool isUnlimited = false)
    {
        var group = new Group { Name = $"g-{Guid.NewGuid():N}", MonthlyTokenQuota = quota, IsUnlimited = isUnlimited };
        Context.Groups.Add(group);
        await Context.SaveChangesAsync();
        Context.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = user.UserId });
        await Context.SaveChangesAsync();
        return group;
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
