using FoundryGate.Core.Quota;
using FoundryGate.Core.Requests;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Core.Quota;

/// <summary>
/// The shared reset (#38/#119) at the level both hosts use it: what a run writes, what it refuses to
/// touch, and that running it again changes nothing. The Api's own entry point (actor resolution,
/// <c>quota.reset</c>) is covered by <c>QuotaAllocationServiceTests</c>; here the trigger is the
/// scheduled one.
/// </summary>
public class QuotaResetServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 1, 0, TimeSpan.Zero);
    private static readonly BillingPeriod Period = new(2026, 10);

    private readonly MutableTimeProvider _clock = new(Now);
    private readonly RecordingGatewayTierSync _tierSync = new();

    [Fact]
    public async Task A_scheduled_reset_creates_the_periods_rows_and_audits_once_as_the_system()
    {
        await SeedReferenceDataAsync();
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);
        var grace = await SeedUserAsync("Grace");

        var outcome = await CreateService().ResetAsync(QuotaResetTrigger.Scheduled(), CancellationToken.None);

        Assert.Equal(2, outcome.UsersResetCount);
        Assert.Equal(Period, outcome.Period);
        Assert.Equal(Now, outcome.ResetDate);

        var rows = await Context.QuotaAllocations.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(Now, row.ResetDate));
        Assert.All(rows, row => Assert.Equal(0, row.TokensUsed));
        Assert.Equal(TestGatewayTiers.PowerCap, rows.Single(r => r.UserId == ada.UserId).AllocatedTokens);
        Assert.Equal(GatewayTiers.Standard, rows.Single(r => r.UserId == grace.UserId).TierProductId); // system default

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditActions.QuotaMonthlyReset, audit.Action);
        Assert.Null(audit.ActorUserId); // a scheduled run has no human actor
        Assert.Contains("\"usersResetCount\":2", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"periodMonth\":10", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_row_keeps_its_reconciled_TokensUsed_and_loses_its_hard_stop()
    {
        await SeedReferenceDataAsync();
        var dev = await SeedUserAsync("Dev", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 1_234_567, isHardStopped: true);

        _ = await CreateService().ResetAsync(QuotaResetTrigger.Scheduled(), CancellationToken.None);

        var row = await Context.QuotaAllocations.AsNoTracking().SingleAsync();

        // The gateway's monthly window resets itself (#10 direction update), so zeroing this mid-period
        // would only make the dashboard disagree with what the gateway is actually counting.
        Assert.Equal(1_234_567, row.TokensUsed);
        Assert.False(row.IsHardStopped);
        Assert.Equal(Now, row.ResetDate);
    }

    [Fact]
    public async Task Running_it_twice_in_a_period_produces_the_same_rows_and_a_second_audit_row()
    {
        await SeedReferenceDataAsync();
        var dev = await SeedUserAsync("Dev");

        var first = await CreateService().ResetAsync(QuotaResetTrigger.Scheduled(), CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(1));
        var second = await CreateService().ResetAsync(QuotaResetTrigger.Scheduled(), CancellationToken.None);

        Assert.Equal(first.UsersResetCount, second.UsersResetCount);
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync(a => a.UserId == dev.UserId));
        Assert.Equal(Now.AddHours(1), (await Context.QuotaAllocations.AsNoTracking().SingleAsync()).ResetDate);
        Assert.Equal(2, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.QuotaMonthlyReset));
    }

    [Fact]
    public async Task Deactivated_users_get_no_allocation()
    {
        await SeedReferenceDataAsync();
        _ = await SeedUserAsync("Gone", u => u.IsActive = false);
        var active = await SeedUserAsync("Here");

        var outcome = await CreateService().ResetAsync(QuotaResetTrigger.Scheduled(), CancellationToken.None);

        Assert.Equal(1, outcome.UsersResetCount);
        Assert.Equal(active.UserId, (await Context.QuotaAllocations.AsNoTracking().SingleAsync()).UserId);
    }

    [Fact]
    public async Task A_scheduled_reset_never_moves_a_subscription_between_tier_products()
    {
        // The reason the Functions host can register NullGatewayTierSync: a reset re-runs resolution over
        // inputs nobody changed, so the tier it computes is the tier already on last period's row. If
        // this ever fails, the Functions registration is wrong and the gateway would silently drift.
        await SeedReferenceDataAsync();
        var dev = await SeedUserAsync("Dev", u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
            u.ApimSubscriptionId = "foundrygate-1";
        });
        await SeedAllocationAsync(dev, new BillingPeriod(2026, 9), allocated: TestGatewayTiers.PowerCap, tokensUsed: 10, tier: GatewayTiers.Power);

        var outcome = await CreateService().ResetAsync(QuotaResetTrigger.Scheduled(), CancellationToken.None);

        Assert.Empty(_tierSync.Calls);
        Assert.Equal(0, outcome.TierSyncCount);
    }

    [Fact]
    public async Task An_admin_trigger_names_the_actor_and_uses_the_manual_action()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");

        _ = await CreateService().ResetAsync(QuotaResetTrigger.Admin(admin.UserId), CancellationToken.None);

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditActions.QuotaAllocationReset, audit.Action);
        Assert.Equal(admin.UserId, audit.ActorUserId);
    }

    [Fact]
    public async Task A_reset_closes_requests_left_pending_from_a_closed_period_in_the_same_unit_of_work()
    {
        // #159: the reset is what keeps the review queue from accumulating a dead entry a month. The
        // sweep's own audit row is separate from the reset's, and both commit with the allocations.
        await SeedReferenceDataAsync();
        var dev = await SeedUserAsync("Dev", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var stale = await SeedRequestAsync(dev, new BillingPeriod(2026, 9), QuotaRequestStatusType.Pending);
        var live = await SeedRequestAsync(dev, Period, QuotaRequestStatusType.Pending);

        var outcome = await CreateService().ResetAsync(QuotaResetTrigger.Scheduled(), CancellationToken.None);

        // Reported on the outcome, not only in the audit log: an admin who runs POST /quota/reset and
        // clears six stale requests should be told so by the response (#204 review).
        Assert.Equal(1, outcome.ExpiredRequestCount);

        await using var verify = CreateVerificationContext();
        var rows = await verify.QuotaIncreaseRequests.AsNoTracking().ToDictionaryAsync(r => r.QuotaIncreaseRequestId);
        Assert.Equal(QuotaRequestStatusType.Rejected, rows[stale].StatusType);
        Assert.Null(rows[stale].ReviewedByUserId);
        Assert.Equal(QuotaRequestStatusType.Pending, rows[live].StatusType);

        var audits = await verify.AuditLogs.AsNoTracking().ToListAsync();
        var sweep = Assert.Single(audits, a => a.Action == AuditActions.QuotaRequestsExpired);
        Assert.Contains("\"expiredCount\":1", sweep.Details, StringComparison.Ordinal);
        var reset = Assert.Single(audits, a => a.Action == AuditActions.QuotaMonthlyReset);
        Assert.Contains("\"expiredRequestCount\":1", reset.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reset_with_nothing_stale_writes_only_its_own_audit_row()
    {
        await SeedReferenceDataAsync();
        var dev = await SeedUserAsync("Dev", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        _ = await SeedRequestAsync(dev, Period, QuotaRequestStatusType.Pending);

        _ = await CreateService().ResetAsync(QuotaResetTrigger.Scheduled(), CancellationToken.None);

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditActions.QuotaMonthlyReset, audit.Action);
        Assert.Contains("\"expiredRequestCount\":0", audit.Details, StringComparison.Ordinal);
    }

    private async Task<int> SeedRequestAsync(User user, BillingPeriod period, QuotaRequestStatusType status)
    {
        var request = new QuotaIncreaseRequest
        {
            UserId = user.UserId,
            RequestedByUserId = user.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            CurrentQuota = TestGatewayTiers.StandardCap,
            RequestedQuota = TestGatewayTiers.PowerCap,
            Justification = "Running a batch evaluation this sprint.",
            StatusType = status,
        };
        Context.QuotaIncreaseRequests.Add(request);
        await Context.SaveChangesAsync();
        Context.Entry(request).State = EntityState.Detached;
        return request.QuotaIncreaseRequestId;
    }

    private QuotaResetService CreateService()
    {
        var resolution = new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance);

        var auditWriter = new AuditWriter(Context, _clock);

        return new QuotaResetService(
            Context,
            resolution,
            new QuotaRequestExpiry(Context, auditWriter, _clock, NullLogger<QuotaRequestExpiry>.Instance),
            auditWriter,
            _clock,
            NullLogger<QuotaResetService>.Instance);
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

    private async Task SeedAllocationAsync(User user, BillingPeriod period, long? allocated, long tokensUsed, bool isHardStopped = false, string tier = GatewayTiers.Standard)
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
        };
        Context.QuotaAllocations.Add(allocation);
        await Context.SaveChangesAsync();
        Context.Entry(allocation).State = EntityState.Detached;
    }
}
