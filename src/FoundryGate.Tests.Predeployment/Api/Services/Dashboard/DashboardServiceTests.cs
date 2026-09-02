using FoundryGate.Api.Services.Dashboard;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.Extensions.Caching.Memory;

namespace FoundryGate.Tests.Predeployment.Api.Services.Dashboard;

/// <summary>
/// <see cref="DashboardService"/> against a private database, where every number can be pinned
/// exactly (#162). The endpoint tests cover the auth matrix and the wire shape.
/// </summary>
public class DashboardServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly FixedCostEstimator _costEstimator = new();

    [Fact]
    public async Task Empty_system_reports_zeroes_and_no_consumers()
    {
        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        Assert.Equal(0, summary.TotalUserCount);
        Assert.Equal(0, summary.ActiveUserCount);
        Assert.Equal(0, summary.UnlimitedUserCount);
        Assert.Equal(0, summary.PendingQuotaIncreaseRequestCount);
        Assert.Equal(0, summary.TotalTokensUsedThisPeriod);
        Assert.Equal(0, summary.HardStoppedUserCount);
        Assert.Equal(0, summary.OverBudgetUserCount);
        Assert.Empty(summary.TopConsumers);
    }

    [Fact]
    public async Task Hard_stopped_counts_this_periods_stopped_allocations_of_active_users_only()
    {
        var stopped = await SeedUserAsync("Stopped");
        var stoppedAndGone = await SeedUserAsync("Stopped and deactivated", isActive: false);
        var running = await SeedUserAsync("Running");
        await SeedAllocationAsync(stopped, tokensUsed: 10, allocatedTokens: 5_000_000, isHardStopped: true);
        // Already off the gateway — counting them would bury the people an admin can still act for.
        await SeedAllocationAsync(stoppedAndGone, tokensUsed: 10, allocatedTokens: 5_000_000, isHardStopped: true);
        await SeedAllocationAsync(running, tokensUsed: 10, allocatedTokens: 5_000_000);
        // Last month's hard stop is last month's problem.
        await SeedAllocationAsync(running, tokensUsed: 10, allocatedTokens: 5_000_000, period: new BillingPeriod(2026, 8), isHardStopped: true);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        Assert.Equal(1, summary.HardStoppedUserCount);
    }

    [Fact]
    public async Task Over_budget_counts_finite_allocations_whose_reconciled_usage_reached_the_cap()
    {
        var atTheCap = await SeedUserAsync("Exactly at the cap");
        var past = await SeedUserAsync("Past it");
        var under = await SeedUserAsync("Under it");
        var unlimited = await SeedUserAsync("Unlimited", isUnlimited: true);
        var departed = await SeedUserAsync("Departed", isActive: false);

        // The gateway refuses the request that would cross the cap, so reaching it is already cut off.
        await SeedAllocationAsync(atTheCap, tokensUsed: 5_000_000, allocatedTokens: 5_000_000);
        await SeedAllocationAsync(past, tokensUsed: 5_000_001, allocatedTokens: 5_000_000);
        await SeedAllocationAsync(under, tokensUsed: 4_999_999, allocatedTokens: 5_000_000);
        // Unlimited can never be over budget, however much it spends.
        await SeedAllocationAsync(unlimited, tokensUsed: 99_000_000, allocatedTokens: null, tier: GatewayTiers.Unlimited);
        await SeedAllocationAsync(departed, tokensUsed: 9_000_000, allocatedTokens: 5_000_000);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        Assert.Equal(2, summary.OverBudgetUserCount);
    }

    [Fact]
    public async Task Over_budget_and_hard_stopped_are_different_questions()
    {
        // Quota exhaustion is the gateway's 403 and never sets IsHardStopped (#7 direction update),
        // so an over-budget developer is not a hard-stopped one — the dashboard must not conflate them.
        var exhausted = await SeedUserAsync("Exhausted");
        await SeedAllocationAsync(exhausted, tokensUsed: 6_000_000, allocatedTokens: 5_000_000);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        Assert.Equal(1, summary.OverBudgetUserCount);
        Assert.Equal(0, summary.HardStoppedUserCount);
    }

    [Fact]
    public async Task Counts_separate_total_active_and_unlimited_users()
    {
        _ = await SeedUserAsync("Active one");
        _ = await SeedUserAsync("Active two");
        _ = await SeedUserAsync("Unlimited", isUnlimited: true);
        _ = await SeedUserAsync("Departed", isActive: false);
        // An inactive account that still carries the unlimited flag must not inflate the "how many
        // people are uncapped" number: it consumes nothing.
        _ = await SeedUserAsync("Departed unlimited", isActive: false, isUnlimited: true);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        Assert.Equal(5, summary.TotalUserCount);
        Assert.Equal(3, summary.ActiveUserCount);
        Assert.Equal(1, summary.UnlimitedUserCount);
    }

    [Fact]
    public async Task Pending_request_count_ignores_reviewed_requests()
    {
        // Two *different* requesters for the two pending rows: one user can hold at most one pending
        // request per period (#147's filtered unique index), and their decided rows are what this is
        // actually checking are excluded from the count.
        var user = await SeedUserAsync("Requester");
        var other = await SeedUserAsync("Other requester");
        await SeedRequestAsync(user, QuotaRequestStatusType.Pending);
        await SeedRequestAsync(other, QuotaRequestStatusType.Pending);
        await SeedRequestAsync(user, QuotaRequestStatusType.Approved);
        await SeedRequestAsync(user, QuotaRequestStatusType.Rejected);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        Assert.Equal(2, summary.PendingQuotaIncreaseRequestCount);
    }

    [Fact]
    public async Task Token_total_covers_this_period_only_and_includes_deactivated_users()
    {
        var alice = await SeedUserAsync("Alice");
        var departed = await SeedUserAsync("Departed", isActive: false);
        await SeedAllocationAsync(alice, tokensUsed: 1_000, allocatedTokens: 5_000_000);
        await SeedAllocationAsync(departed, tokensUsed: 500, allocatedTokens: 5_000_000);
        // Last month's row must not be counted.
        await SeedAllocationAsync(alice, tokensUsed: 9_999_999, allocatedTokens: 5_000_000, period: new BillingPeriod(2026, 8));

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        Assert.Equal(1_500, summary.TotalTokensUsedThisPeriod);
    }

    // -- Estimated cost (#177) --

    [Fact]
    public async Task No_rate_card_means_no_estimated_cost_anywhere()
    {
        var dev = await SeedUserAsync("Dev");
        await SeedAllocationAsync(dev, tokensUsed: 1_000_000, allocatedTokens: 5_000_000);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        // Null, not zero: a fork that has not priced its tokens has no cost to report, and a zero
        // would render as "free".
        Assert.Null(summary.EstimatedCostThisPeriod);
        Assert.All(summary.TopConsumers, c => Assert.Null(c.EstimatedCostThisPeriod));
    }

    [Fact]
    public async Task A_configured_rate_card_prices_the_period_total_and_every_consumer()
    {
        _costEstimator.RateCard = FixedCostEstimator.Blended(10m);
        var heavy = await SeedUserAsync("Heavy");
        var light = await SeedUserAsync("Light");
        await SeedAllocationAsync(heavy, tokensUsed: 3_000_000, allocatedTokens: 5_000_000);
        await SeedAllocationAsync(light, tokensUsed: 500_000, allocatedTokens: 5_000_000);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        // 3.5M tokens at 10 per million. The consumers' figures must add up to the total, or the
        // dashboard contradicts itself twice on the same screen.
        Assert.Equal(35m, summary.EstimatedCostThisPeriod);
        Assert.Equal(30m, summary.TopConsumers.Single(c => c.DisplayName == "Heavy").EstimatedCostThisPeriod);
        Assert.Equal(5m, summary.TopConsumers.Single(c => c.DisplayName == "Light").EstimatedCostThisPeriod);
    }

    [Fact]
    public async Task Top_consumers_are_the_ten_busiest_active_users_this_period()
    {
        for (var i = 1; i <= 12; i++)
        {
            var user = await SeedUserAsync($"User {i:D2}");
            await SeedAllocationAsync(user, tokensUsed: i * 1_000, allocatedTokens: 5_000_000);
        }

        // Busiest of all, but deactivated — the list is about who to talk to, and there is nobody
        // to talk to here.
        var departed = await SeedUserAsync("Departed", isActive: false);
        await SeedAllocationAsync(departed, tokensUsed: 999_999, allocatedTokens: 5_000_000);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        Assert.Equal(DashboardService.TopConsumerCount, summary.TopConsumers.Count);
        Assert.Equal(
            ["User 12", "User 11", "User 10", "User 09", "User 08", "User 07", "User 06", "User 05", "User 04", "User 03"],
            summary.TopConsumers.Select(c => c.DisplayName));
        Assert.Equal(12_000, summary.TopConsumers[0].TokensUsed);
        Assert.DoesNotContain(summary.TopConsumers, c => c.DisplayName == "Departed");
    }

    [Fact]
    public async Task Consumer_percentage_is_null_when_unlimited_and_computed_otherwise()
    {
        var capped = await SeedUserAsync("Capped");
        var unlimited = await SeedUserAsync("Unlimited", isUnlimited: true);
        var zeroQuota = await SeedUserAsync("Zero quota");
        await SeedAllocationAsync(capped, tokensUsed: 1_000_000, allocatedTokens: 5_000_000);
        await SeedAllocationAsync(unlimited, tokensUsed: 900_000, allocatedTokens: null, tier: GatewayTiers.Unlimited);
        await SeedAllocationAsync(zeroQuota, tokensUsed: 1, allocatedTokens: 0);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);
        var byName = summary.TopConsumers.ToDictionary(c => c.DisplayName);

        Assert.Equal(20d, byName["Capped"].PercentUsed);
        Assert.Equal(5_000_000, byName["Capped"].AllocatedTokens);
        Assert.Null(byName["Unlimited"].PercentUsed);
        Assert.Null(byName["Unlimited"].AllocatedTokens);
        // A zero quota reads as fully used the moment anything is spent — never a division by zero.
        Assert.Equal(100d, byName["Zero quota"].PercentUsed);
    }

    [Fact]
    public async Task Consumers_carry_the_stable_user_identifiers_the_ui_links_on()
    {
        var user = await SeedUserAsync("Linkable");
        await SeedAllocationAsync(user, tokensUsed: 42, allocatedTokens: 5_000_000);

        var summary = await CreateService().GetSummaryAsync(fresh: true, CancellationToken.None);

        var consumer = Assert.Single(summary.TopConsumers);
        Assert.Equal(user.UserId, consumer.UserId);
        Assert.Equal(user.UserUnique, consumer.UserUnique);
    }

    [Fact]
    public async Task A_second_call_is_served_from_cache_until_fresh_is_asked_for()
    {
        var service = CreateService();
        _ = await SeedUserAsync("First");
        var first = await service.GetSummaryAsync(fresh: false, CancellationToken.None);

        _ = await SeedUserAsync("Second");
        var cached = await service.GetSummaryAsync(fresh: false, CancellationToken.None);
        var fresh = await service.GetSummaryAsync(fresh: true, CancellationToken.None);

        Assert.Equal(1, first.TotalUserCount);
        Assert.Same(first, cached);
        Assert.Equal(2, fresh.TotalUserCount);

        // ...and the fresh read replaces what the cache serves next.
        Assert.Equal(2, (await service.GetSummaryAsync(fresh: false, CancellationToken.None)).TotalUserCount);
    }

    [Fact]
    public async Task A_new_billing_period_never_serves_the_previous_periods_cached_summary()
    {
        var user = await SeedUserAsync("Alice");
        await SeedAllocationAsync(user, tokensUsed: 7_000, allocatedTokens: 5_000_000);
        var service = CreateService();

        var september = await service.GetSummaryAsync(fresh: false, CancellationToken.None);
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var october = await service.GetSummaryAsync(fresh: false, CancellationToken.None);

        Assert.Equal(7_000, september.TotalTokensUsedThisPeriod);
        Assert.Equal(0, october.TotalTokensUsedThisPeriod);
        Assert.Empty(october.TopConsumers);
    }

    private DashboardService CreateService() => new(Context, _cache, _costEstimator, _timeProvider);

    private async Task<User> SeedUserAsync(string displayName, bool isActive = true, bool isUnlimited = false)
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Email = $"{Guid.NewGuid():N}@contoso.test",
            IsActive = isActive,
            IsUnlimited = isUnlimited,
        };

        Context.Users.Add(user);
        await Context.SaveChangesAsync(CancellationToken.None);
        return user;
    }

    private async Task SeedAllocationAsync(
        User user,
        long tokensUsed,
        long? allocatedTokens,
        BillingPeriod? period = null,
        string tier = GatewayTiers.Standard,
        bool isHardStopped = false)
    {
        var target = period ?? BillingPeriod.FromInstant(Now);

        Context.QuotaAllocations.Add(new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = target.Year,
            PeriodMonth = target.Month,
            AllocatedTokens = allocatedTokens,
            TokensUsed = tokensUsed,
            IsHardStopped = isHardStopped,
            ResolvedLevelType = QuotaLevelType.SystemDefault,
            TierProductId = tier,
        });
        await Context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task SeedRequestAsync(User user, QuotaRequestStatusType status)
    {
        Context.QuotaIncreaseRequests.Add(new QuotaIncreaseRequest
        {
            UserId = user.UserId,
            RequestedByUserId = user.UserId,
            PeriodYear = Now.Year,
            PeriodMonth = Now.Month,
            RequestedQuota = 20_000_000,
            Justification = "Because the agents are hungry.",
            StatusType = status,
        });
        await Context.SaveChangesAsync(CancellationToken.None);
    }
}
