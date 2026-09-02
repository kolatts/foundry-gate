using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Quota;
using FoundryGate.Functions.Services.Usage;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Functions.Services.Usage;

/// <summary>
/// <see cref="UsageSyncJob"/> — the reconciliation half of the system (#39/#84). Every test here is
/// about the same distinction: this job mirrors what the gateway already enforced, and must never
/// enforce anything itself.
/// </summary>
public class UsageSyncJobTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);
    private static readonly BillingPeriod Period = new(2026, 10);

    private readonly MutableTimeProvider _clock = new(Now);

    [Fact]
    public async Task It_overwrites_TokensUsed_from_the_gateways_own_totals()
    {
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 100);
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: 4_200));

        var outcome = await CreateJob(query).RunAsync(CancellationToken.None);

        Assert.Equal(Period, Assert.Single(query.Queried));
        Assert.Equal(1, outcome.SubscriptionsSeen);
        Assert.Equal(1, outcome.AllocationsUpdated);
        Assert.Equal(4_200, (await Context.QuotaAllocations.AsNoTracking().SingleAsync()).TokensUsed);
    }

    [Fact]
    public async Task Re_running_the_same_pass_converges_rather_than_accumulating()
    {
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 0);
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: 4_200));

        _ = await CreateJob(query).RunAsync(CancellationToken.None);
        var second = await CreateJob(query).RunAsync(CancellationToken.None);

        // Period totals, assigned not added — which is what makes a catch-up after an outage safe.
        Assert.Equal(4_200, (await Context.QuotaAllocations.AsNoTracking().SingleAsync()).TokensUsed);
        Assert.Equal(0, second.AllocationsUpdated); // nothing changed the second time
    }

    [Fact]
    public async Task Usage_over_a_finite_allocation_is_reported_as_drift_and_never_hard_stops_anyone()
    {
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 0);
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: TestGatewayTiers.StandardCap + 500));
        var logs = new CapturingLoggerProvider();

        var outcome = await CreateJob(query, logs).RunAsync(CancellationToken.None);

        Assert.Equal(1, outcome.DriftCount);
        Assert.Contains(logs.Entries, entry => entry.Contains("Usage drift", StringComparison.Ordinal));

        // Enforcement is the gateway's 403; IsHardStopped means offboarding (#7 direction update).
        Assert.False((await Context.QuotaAllocations.AsNoTracking().SingleAsync()).IsHardStopped);
    }

    [Fact]
    public async Task An_unlimited_allocation_can_never_drift()
    {
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: null, tokensUsed: 0, tier: GatewayTiers.Unlimited);
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: 999_999_999));

        var outcome = await CreateJob(query).RunAsync(CancellationToken.None);

        Assert.Equal(0, outcome.DriftCount);
        Assert.Equal(999_999_999, (await Context.QuotaAllocations.AsNoTracking().SingleAsync()).TokensUsed);
    }

    [Fact]
    public async Task Subscriptions_that_map_to_no_user_are_counted_and_ignored()
    {
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 0);
        var query = new FakeUsageQueryClient(
            Usage(dev.UserId, total: 10),
            new SubscriptionUsage("master", 1, 1, 2, 1),                 // APIM's built-in subscription
            new SubscriptionUsage("hand-made-by-an-admin", 5, 5, 10, 2)); // someone else on the gateway

        var outcome = await CreateJob(query).RunAsync(CancellationToken.None);

        Assert.Equal(3, outcome.SubscriptionsSeen);
        Assert.Equal(2, outcome.UnknownSubscriptions);
        Assert.Equal(1, outcome.AllocationsUpdated);
        Assert.Equal(10, (await Context.QuotaAllocations.AsNoTracking().SingleAsync()).TokensUsed);
    }

    [Fact]
    public async Task A_developer_with_no_allocation_row_yet_is_skipped_without_failing_the_pass()
    {
        var dev = await SeedUserAsync("Ada"); // spent tokens before their first /me of the month
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: 10));

        var outcome = await CreateJob(query).RunAsync(CancellationToken.None);

        Assert.Equal(1, outcome.SubscriptionsSeen);
        Assert.Equal(0, outcome.AllocationsUpdated);
        Assert.Empty(await Context.QuotaAllocations.AsNoTracking().ToListAsync()); // the sync never mints rows
    }

    [Fact]
    public async Task A_pass_that_changed_something_writes_exactly_one_audit_row_with_the_run_counts()
    {
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 0);
        var query = new FakeUsageQueryClient(
            Usage(dev.UserId, total: TestGatewayTiers.StandardCap + 1),
            new SubscriptionUsage("master", 0, 0, 3, 1));

        _ = await CreateJob(query).RunAsync(CancellationToken.None);

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditActions.UsageSynced, audit.Action);
        Assert.Null(audit.ActorUserId);
        Assert.Contains("\"subscriptionsSeen\":2", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"allocationsUpdated\":1", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"unknownSubscriptions\":1", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"driftCount\":1", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"periodYear\":2026", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"periodMonth\":10", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Five_consecutive_ticks_with_four_no_ops_write_one_audit_row()
    {
        // The review's probe, pinned. The old guard was `usage.Count == 0 && updated == 0`, but the
        // query returns month-to-date TOTALS, so after the first request of the month usage.Count is
        // never zero again and every one of the 96 daily ticks wrote a row — the ~35k/year D-016 exists
        // to prevent.
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 0);
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: 4_200));

        for (var tick = 0; tick < 5; tick++)
        {
            _ = await CreateJob(query).RunAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(15));
        }

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditActions.UsageSynced, audit.Action);
        Assert.Contains("\"allocationsUpdated\":1", audit.Details, StringComparison.Ordinal); // the first tick
    }

    [Fact]
    public async Task A_pass_with_no_traffic_at_all_writes_one_row_the_first_time_and_none_after()
    {
        // The very first recorded pass is worth a row even when it is all zeroes: it is what tells an
        // operator the job is wired up. After that, silence.
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 0);
        var query = new FakeUsageQueryClient();

        var first = await CreateJob(query).RunAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(15));
        _ = await CreateJob(query).RunAsync(CancellationToken.None);

        Assert.Equal(0, first.SubscriptionsSeen);
        Assert.Single(await Context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_newly_over_budget_developer_is_recorded_even_though_their_usage_did_not_move()
    {
        // An admin cutting someone's quota makes them drift without a single token changing hands.
        // Gating the audit row on `updated` alone would have swallowed it.
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.PowerCap, tokensUsed: 0);
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: TestGatewayTiers.StandardCap + 1));

        _ = await CreateJob(query).RunAsync(CancellationToken.None); // in budget on Power
        _clock.Advance(TimeSpan.FromMinutes(15));

        var row = await Context.QuotaAllocations.SingleAsync();
        row.AllocatedTokens = TestGatewayTiers.StandardCap; // demoted to Standard
        await Context.SaveChangesAsync();

        var second = await CreateJob(query).RunAsync(CancellationToken.None);

        Assert.Equal(0, second.AllocationsUpdated); // nothing about their usage changed
        Assert.Equal(1, second.DriftCount);
        Assert.Equal(2, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.UsageSynced));
    }

    [Fact]
    public async Task A_newly_unknown_subscription_is_recorded_even_with_nothing_else_to_change()
    {
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 4_200);
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: 4_200));

        _ = await CreateJob(query).RunAsync(CancellationToken.None); // baseline: nothing to update
        _clock.Advance(TimeSpan.FromMinutes(15));
        query.Rows.Add(new SubscriptionUsage("someone-elses-subscription", 1, 1, 2, 1));

        var second = await CreateJob(query).RunAsync(CancellationToken.None);

        Assert.Equal(0, second.AllocationsUpdated);
        Assert.Equal(1, second.UnknownSubscriptions);
        Assert.Equal(2, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.UsageSynced));
    }

    [Fact]
    public async Task Early_in_a_month_the_just_closed_period_is_reconciled_too()
    {
        // Otherwise the old month's final pass — plus Log Analytics ingestion lag — is missing from it
        // permanently, because BillingPeriod.Current never looks back.
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.Zero));
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 1_000); // October
        await SeedAllocationAsync(dev, new BillingPeriod(2026, 11), allocated: TestGatewayTiers.StandardCap, tokensUsed: 0);
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: 9_999));

        var outcome = await CreateJob(query).RunAsync(CancellationToken.None);

        Assert.True(outcome.IncludedPreviousPeriod);
        Assert.Equal([new BillingPeriod(2026, 11), Period], query.Queried);

        var rows = await Context.QuotaAllocations.AsNoTracking().ToListAsync();
        Assert.Equal(9_999, rows.Single(r => r.PeriodMonth == 10).TokensUsed); // the late October traffic landed
        Assert.Equal(9_999, rows.Single(r => r.PeriodMonth == 11).TokensUsed);
        Assert.Equal(2, outcome.AllocationsUpdated);
    }

    [Fact]
    public async Task Past_the_grace_window_the_closed_month_is_left_alone()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 11, UsageSyncJob.PreviousPeriodGraceDays + 1, 0, 30, 0, TimeSpan.Zero));
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 1_000); // October
        await SeedAllocationAsync(dev, new BillingPeriod(2026, 11), allocated: TestGatewayTiers.StandardCap, tokensUsed: 0);
        var query = new FakeUsageQueryClient(Usage(dev.UserId, total: 9_999));

        var outcome = await CreateJob(query).RunAsync(CancellationToken.None);

        Assert.False(outcome.IncludedPreviousPeriod);
        Assert.Equal([new BillingPeriod(2026, 11)], query.Queried);
        Assert.Equal(1_000, (await Context.QuotaAllocations.AsNoTracking().SingleAsync(r => r.PeriodMonth == 10)).TokensUsed);
    }

    [Fact]
    public async Task Only_the_current_period_is_touched()
    {
        var dev = await SeedUserAsync("Ada");
        await SeedAllocationAsync(dev, new BillingPeriod(2026, 9), allocated: TestGatewayTiers.StandardCap, tokensUsed: 777);
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 0);

        _ = await CreateJob(new FakeUsageQueryClient(Usage(dev.UserId, total: 42))).RunAsync(CancellationToken.None);

        var rows = await Context.QuotaAllocations.AsNoTracking().ToListAsync();
        Assert.Equal(777, rows.Single(r => r.PeriodMonth == 9).TokensUsed); // last month is closed
        Assert.Equal(42, rows.Single(r => r.PeriodMonth == 10).TokensUsed);
    }

    private static SubscriptionUsage Usage(int userId, long total) =>
        new(ApimSubscriptionNames.ForUser(userId), total / 2, total - (total / 2), total, 1);

    private UsageSyncJob CreateJob(FakeUsageQueryClient query, CapturingLoggerProvider? logs = null) =>
        new(
            Context,
            query,
            new AuditWriter(Context, _clock),
            _clock,
            logs?.CreateLogger<UsageSyncJob>() ?? NullLogger<UsageSyncJob>.Instance);

    private async Task<User> SeedUserAsync(string displayName)
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Email = $"{Guid.NewGuid():N}@contoso.test",
        };
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        user.ApimSubscriptionId = ApimSubscriptionNames.ForUser(user.UserId);
        await Context.SaveChangesAsync();
        return user;
    }

    private async Task SeedAllocationAsync(User user, BillingPeriod period, long? allocated, long tokensUsed, string tier = GatewayTiers.Standard)
    {
        var allocation = new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            AllocatedTokens = allocated,
            TokensUsed = tokensUsed,
            ResolvedLevelType = QuotaLevelType.SystemDefault,
            TierProductId = tier,
        };
        Context.QuotaAllocations.Add(allocation);
        await Context.SaveChangesAsync();
        Context.Entry(allocation).State = EntityState.Detached;
    }
}
