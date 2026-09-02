using System.Globalization;
using Azure;
using FoundryGate.Core.Quota;
using FoundryGate.Core.Requests;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Quota;
using FoundryGate.Functions.Services.Quota;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        var resetLock = new FakeJobLock();

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
    public async Task Before_the_configured_day_it_does_nothing_at_all_and_never_reaches_the_lock()
    {
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        await SetResetDayAsync("17");
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 3, 0, 1, 0, TimeSpan.Zero));
        var resetLock = new FakeJobLock();

        var outcome = await CreateJob(resetLock).RunAsync(CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.Equal(MonthlyResetSkipReasonType.BeforeTheConfiguredDay, outcome.SkipReasonType);
        Assert.Equal(3, outcome.DayOfMonth);
        Assert.Equal(17, outcome.ConfiguredDayOfMonth);
        Assert.Empty(resetLock.Requested);
        Assert.Empty(await Context.QuotaAllocations.AsNoTracking().ToListAsync());
        Assert.Empty(await Context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Once_the_period_is_reset_the_remaining_daily_ticks_do_nothing()
    {
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        var resetLock = new FakeJobLock();

        _ = await CreateJob(resetLock).RunAsync(CancellationToken.None); // the 1st

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 2, 0, 1, 0, TimeSpan.Zero));
        var second = await CreateJob(resetLock).RunAsync(CancellationToken.None);

        Assert.False(second.Ran);
        Assert.Equal(MonthlyResetSkipReasonType.AlreadyResetThisPeriod, second.SkipReasonType);
        Assert.Single(resetLock.Requested); // the second tick never even asked for the lock
        Assert.Equal(1, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.QuotaMonthlyReset));
    }

    [Fact]
    public async Task A_tick_lost_to_an_outage_is_picked_up_the_next_day_rather_than_costing_the_month()
    {
        // The failure the equality gate used to turn into a lost month: nothing ran on the 1st.
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 2, 0, 1, 0, TimeSpan.Zero));

        var outcome = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(2, outcome.DayOfMonth);
        Assert.Equal(1, outcome.ConfiguredDayOfMonth);
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync());
        Assert.Equal(1, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.QuotaMonthlyReset));
    }

    [Fact]
    public async Task Last_months_reset_does_not_satisfy_this_month()
    {
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");

        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero));
        _ = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 1, 0, 1, 0, TimeSpan.Zero));
        var october = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        Assert.True(october.Ran);
        Assert.Equal(new BillingPeriod(2026, 10), october.Reset!.Value.Period);
        Assert.Equal(2, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.QuotaMonthlyReset));
    }

    [Fact]
    public async Task An_admins_manual_reset_does_not_suppress_the_scheduled_run()
    {
        // Different action, different actor: the trail is expected to show the scheduled run monthly.
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        Context.AuditLogs.Add(new AuditLog
        {
            Action = AuditActions.QuotaAllocationReset,
            ActorUserId = admin.UserId,
            TargetType = string.Empty,
            TargetId = string.Empty,
            Details = string.Empty,
            OccurredDate = _clock.GetUtcNow(),
        });
        await Context.SaveChangesAsync();

        var outcome = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
    }

    [Fact]
    public async Task Changing_ResetDayOfMonth_moves_the_reset_without_a_redeploy()
    {
        // The point of #165: the key was seeded, validated and listed, and read by nothing.
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        await SetResetDayAsync("17");
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 17, 0, 1, 0, TimeSpan.Zero));

        var outcome = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

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

        var outcome = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(MonthlyResetJob.DefaultResetDayOfMonth, outcome.ConfiguredDayOfMonth);
    }

    [Fact]
    public async Task A_missing_ResetDayOfMonth_row_still_resets_on_the_first()
    {
        // No reference-data seed at all — an unseeded fork must not silently stop resetting.
        await SeedUserAsync("Ada");
        await SeedDefaultQuotaAsync();

        var outcome = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(MonthlyResetJob.DefaultResetDayOfMonth, outcome.ConfiguredDayOfMonth);
    }

    [Fact]
    public async Task When_another_replica_holds_the_lock_it_writes_nothing()
    {
        await SeedReferenceDataAsync();
        await SeedUserAsync("Ada");
        var resetLock = new FakeJobLock(acquire: false);

        var outcome = await CreateJob(resetLock).RunAsync(CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.Equal(MonthlyResetSkipReasonType.LockHeldElsewhere, outcome.SkipReasonType);
        Assert.Equal(0, resetLock.Released); // nothing was acquired, so nothing to release
        Assert.Empty(await Context.QuotaAllocations.AsNoTracking().ToListAsync());
        Assert.Empty(await Context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_changed_DefaultMonthlyTokenQuota_moves_the_subscription_on_the_next_reset_and_audits_it()
    {
        // The hole the review found: `PUT /config` on DefaultMonthlyTokenQuota re-resolves nobody, so
        // the scheduled reset is the first thing to notice. Until #194 this host could only log that
        // SQL and the gateway now disagree; it now re-scopes the subscription itself, in the reset's
        // own unit of work, with a system-attributed key.tier-changed row beside the run's own.
        await SeedReferenceDataAsync();
        var dev = await SeedUserAsync("Ada");
        var subscriptionName = ApimSubscriptionNames.ForUser(dev.UserId);
        var apim = new FakeApimManagementClient();
        _ = apim.Seed(subscriptionName, GatewayTiers.Standard);
        dev.ApimSubscriptionId = apim.GetSubscriptionResourceId(subscriptionName);
        await Context.SaveChangesAsync();

        await SeedAllocationAsync(dev, new BillingPeriod(2026, 9), TestGatewayTiers.StandardCap, GatewayTiers.Standard);
        await SetConfigAsync(SystemConfigurationKeys.DefaultMonthlyTokenQuota, TestGatewayTiers.PowerCap.ToString(CultureInfo.InvariantCulture));

        var outcome = await CreateJob(new FakeJobLock(), TierSync(apim)).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(1, outcome.Reset!.Value.TierSyncCount);
        Assert.Equal(GatewayTiers.Power, apim.ProductOf(subscriptionName));

        var audit = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.QuotaMonthlyReset);
        Assert.Contains("\"tierChangeCount\":1", audit.Details, StringComparison.Ordinal);

        // System-attributed, because a timer trigger is nobody's request.
        var tierChanged = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.KeyTierChanged);
        Assert.Null(tierChanged.ActorUserId);
    }

    [Fact]
    public async Task A_gateway_that_refuses_one_developers_move_leaves_that_one_alone_and_finishes_the_run()
    {
        // The trade #194 originally made — abort the whole run — meant one subscription deleted out of
        // band in the APIM portal deterministically failed every developer's reset, on every retry,
        // forever (#211 review). The refusal is now reported and skipped: the refused developer keeps the
        // tier the gateway still enforces, so SQL and APIM never disagree, and everybody else resets.
        await SeedReferenceDataAsync();
        var refused = await SeedUserAsync("Refused");
        var moved = await SeedUserAsync("Moved");
        var apim = new FakeApimManagementClient();
        foreach (var dev in new[] { refused, moved })
        {
            var name = ApimSubscriptionNames.ForUser(dev.UserId);
            _ = apim.Seed(name, GatewayTiers.Standard);
            dev.ApimSubscriptionId = apim.GetSubscriptionResourceId(name);
            await SeedAllocationAsync(dev, new BillingPeriod(2026, 9), TestGatewayTiers.StandardCap, GatewayTiers.Standard);
        }

        await Context.SaveChangesAsync();
        await SetConfigAsync(SystemConfigurationKeys.DefaultMonthlyTokenQuota, TestGatewayTiers.PowerCap.ToString(CultureInfo.InvariantCulture));

        // Only the first developer's subscription is missing from the gateway.
        Assert.True(apim.Remove(ApimSubscriptionNames.ForUser(refused.UserId)));

        var logs = new CapturingLoggerProvider();
        var outcome = await CreateJob(new FakeJobLock(), TierSync(apim), logs.CreateLogger<QuotaResetService>()).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(1, outcome.Reset!.Value.TierSyncCount);
        Assert.Equal(1, outcome.Reset!.Value.TierSyncFailureCount);

        // The developer whose move landed is committed, moved and audited...
        Assert.Equal(GatewayTiers.Power, apim.ProductOf(ApimSubscriptionNames.ForUser(moved.UserId)));
        await using var verification = CreateVerificationContext();
        var period = outcome.Reset!.Value.Period;
        var movedRow = await ResetRowAsync(verification, moved, period);
        Assert.Equal(GatewayTiers.Power, movedRow.TierProductId);
        Assert.Equal(TestGatewayTiers.PowerCap, movedRow.AllocatedTokens);
        _ = await verification.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.KeyTierChanged);

        // ...and the refused one gets no row for the new period at all. That is the honest outcome:
        // writing one would claim a budget the gateway is not enforcing, and their previous period's row
        // — which still matches the gateway — is untouched. Their next /me, or the next reset, retries.
        Assert.Empty(await verification.QuotaAllocations.AsNoTracking()
            .Where(a => a.UserId == refused.UserId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month)
            .ToListAsync());
        var refusedPrevious = await ResetRowAsync(verification, refused, new BillingPeriod(2026, 9));
        Assert.Equal(GatewayTiers.Standard, refusedPrevious.TierProductId);
        Assert.Equal(TestGatewayTiers.StandardCap, refusedPrevious.AllocatedTokens);

        // The run completed, said so, and named the developer an operator has to look at.
        var audit = await verification.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.QuotaMonthlyReset);
        Assert.Contains("\"tierChangeCount\":1", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"tierChangeFailureCount\":1", audit.Details, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, entry => entry.Contains($"moving user {refused.UserId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_move_that_fails_after_earlier_ones_landed_keeps_every_earlier_developer_committed_and_audited()
    {
        // The Major in the #211 review: the moves happened inside the loop and the single save ran after
        // it, so a failure on developer N discarded the key.tier-changed rows of 1..N-1 whose
        // subscriptions APIM had already re-scoped — SQL on the old tier, the gateway on the new one, and
        // nothing recording it. Each move now commits as it lands.
        await SeedReferenceDataAsync();
        var devs = new List<User>();
        for (var i = 0; i < 3; i++)
        {
            var dev = await SeedUserAsync($"Dev {i}");
            devs.Add(dev);
        }

        var apim = new FakeApimManagementClient();
        foreach (var dev in devs)
        {
            var name = ApimSubscriptionNames.ForUser(dev.UserId);
            _ = apim.Seed(name, GatewayTiers.Standard);
            dev.ApimSubscriptionId = apim.GetSubscriptionResourceId(name);
            await SeedAllocationAsync(dev, new BillingPeriod(2026, 9), TestGatewayTiers.StandardCap, GatewayTiers.Standard);
        }

        await Context.SaveChangesAsync();
        await SetConfigAsync(SystemConfigurationKeys.DefaultMonthlyTokenQuota, TestGatewayTiers.PowerCap.ToString(CultureInfo.InvariantCulture));

        // Users are processed in UserId order, so break the gateway once the first two have moved.
        var moves = 0;
        apim.AfterMutation = () =>
        {
            if (++moves == 2)
            {
                apim.ThrowOnUpdateScope = new RequestFailedException(429, "Too many requests.");
            }
        };

        var outcome = await CreateJob(new FakeJobLock(), TierSync(apim)).RunAsync(CancellationToken.None);

        Assert.Equal(2, outcome.Reset!.Value.TierSyncCount);
        Assert.Equal(1, outcome.Reset!.Value.TierSyncFailureCount);

        await using var verification = CreateVerificationContext();
        var tierChanged = await verification.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.KeyTierChanged)
            .ToListAsync();

        // Two moves landed; both are committed and both left a row.
        Assert.Equal(2, tierChanged.Count);
        var period = outcome.Reset!.Value.Period;
        foreach (var dev in devs.Take(2))
        {
            Assert.Equal(GatewayTiers.Power, apim.ProductOf(ApimSubscriptionNames.ForUser(dev.UserId)));
            Assert.Equal(GatewayTiers.Power, (await ResetRowAsync(verification, dev, period)).TierProductId);
        }

        // The third never moved, so it has no row for the new period and the gateway is untouched.
        Assert.Equal(GatewayTiers.Standard, apim.ProductOf(ApimSubscriptionNames.ForUser(devs[2].UserId)));
        Assert.Empty(await verification.QuotaAllocations.AsNoTracking()
            .Where(a => a.UserId == devs[2].UserId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month)
            .ToListAsync());
    }

    [Fact]
    public async Task A_reset_over_unchanged_inputs_moves_nobodys_tier()
    {
        // The original claim, now scoped to what it is actually true of: per-user inputs.
        await SeedReferenceDataAsync();
        var dev = await SeedUserAsync("Ada", u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
            u.ApimSubscriptionId = "foundrygate-1";
        });
        await SeedAllocationAsync(dev, new BillingPeriod(2026, 9), TestGatewayTiers.PowerCap, GatewayTiers.Power);

        var outcome = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        Assert.Empty(_tierSync.Calls);
        Assert.Equal(0, outcome.Reset!.Value.TierSyncCount);
    }

    /// <summary>The allocation the run wrote for <paramref name="user"/> — by period, because the seeded previous month's row is still there.</summary>
    private static Task<QuotaAllocation> ResetRowAsync(AppDbContext verification, User user, BillingPeriod period) =>
        verification.QuotaAllocations.AsNoTracking()
            .SingleAsync(a => a.UserId == user.UserId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month);

    /// <summary>The real Core tier sync over an in-memory APIM, wired as the Functions host wires it (system actor).</summary>
    private ApimGatewayTierSync TierSync(FakeApimManagementClient apim) =>
        new(apim, new AuditWriter(Context, _clock), new SystemGatewayTierSyncActor(), NullLogger<ApimGatewayTierSync>.Instance);

    private MonthlyResetJob CreateJob(FakeJobLock resetLock, IGatewayTierSync? tierSync = null, ILogger<QuotaResetService>? resetLogger = null)
    {
        var sync = tierSync ?? _tierSync;

        // Resolution takes the sync too, but the reset always runs it Deferred and drives the moves
        // itself, so the instance resolution holds is never the one that reaches APIM here (#211 review).
        var resolution = new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), sync, NullLogger<QuotaResolutionService>.Instance);
        var auditWriter = new AuditWriter(Context, _clock);
        var reset = new QuotaResetService(
            Context,
            resolution,
            sync,
            new QuotaRequestExpiry(Context, auditWriter, _clock, NullLogger<QuotaRequestExpiry>.Instance),
            auditWriter,
            _clock,
            resetLogger ?? NullLogger<QuotaResetService>.Instance);

        return new MonthlyResetJob(Context, reset, resetLock, _clock, NullLogger<MonthlyResetJob>.Instance);
    }

    private Task SetResetDayAsync(string value) =>
        SetConfigAsync(SystemConfigurationKeys.ResetDayOfMonth, value);

    private async Task SetConfigAsync(string key, string value)
    {
        var row = await Context.SystemConfigurations.SingleAsync(c => c.Key == key);
        row.Value = value;
        await Context.SaveChangesAsync();
    }

    /// <summary>The one configuration row resolution itself needs, for the tests that skip the full seed.</summary>
    private async Task SeedDefaultQuotaAsync()
    {
        Context.SystemConfigurations.Add(new SystemConfiguration
        {
            Key = SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            Value = TestGatewayTiers.StandardCap.ToString(CultureInfo.InvariantCulture),
        });
        await Context.SaveChangesAsync();
    }

    /// <summary>An earlier period's row, so resolution has a "previous tier" to compare this period against.</summary>
    private async Task SeedAllocationAsync(User user, BillingPeriod period, long? allocated, string tier)
    {
        var allocation = new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            AllocatedTokens = allocated,
            ResolvedLevelType = QuotaLevelType.SystemDefault,
            TierProductId = tier,
        };
        Context.QuotaAllocations.Add(allocation);
        await Context.SaveChangesAsync();
        Context.Entry(allocation).State = EntityState.Detached;
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
