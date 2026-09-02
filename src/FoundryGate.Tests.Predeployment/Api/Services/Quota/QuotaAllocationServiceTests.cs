using System.Security.Claims;
using System.Text.Json;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Core.Quota;
using FoundryGate.Core.Requests;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Quota;

/// <summary>
/// The <c>/quota</c> orchestration (#33) over the real resolution service, accessor, audit writer and a
/// movable clock: <c>/me</c> auto-creation (active users only), the admin reads, the tier list, and the
/// idempotent reset's exact preserve/clear/audit semantics — including losing a race to a concurrent writer.
/// </summary>
public class QuotaAllocationServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BillingPeriod Period = new(2026, 9);

    /// <summary>An all-null <see cref="QuotaAllocationQuery"/> — the unfiltered list (#208). The filters themselves are covered end to end in <c>Api/Endpoints/QuotaEndpointTests</c>.</summary>
    private static readonly QuotaAllocationQuery NoFilter = new(null, null, null, null, null);

    private readonly MutableTimeProvider _clock = new(Now);
    private readonly FixedCostEstimator _costEstimator = new();
    private readonly RecordingGatewayTierSync _tierSync = new();

    // -- ListTiers --

    [Fact]
    public void ListTiers_returns_finite_tiers_ascending_then_unlimited_with_display_names()
    {
        var tiers = CreateService("any").ListTiers();

        Assert.Equal([GatewayTiers.Standard, GatewayTiers.Power, GatewayTiers.Unlimited], tiers.Select(t => t.ProductId));
        Assert.Equal(["Standard", "Power", "Unlimited"], tiers.Select(t => t.DisplayName));
        Assert.Equal([TestGatewayTiers.StandardCap, TestGatewayTiers.PowerCap, null], tiers.Select(t => t.MonthlyTokenQuota));
        Assert.Equal([false, false, true], tiers.Select(t => t.IsUnlimited));
    }

    // -- GetMyAllocationAsync --

    [Fact]
    public async Task GetMyAllocationAsync_creates_and_saves_the_callers_allocation_on_first_call_then_returns_the_same_row()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada Lovelace", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);
        var service = CreateService(me.EntraObjectId);

        var first = await service.GetMyAllocationAsync(CancellationToken.None);
        var second = await service.GetMyAllocationAsync(CancellationToken.None);

        Assert.Equal(first.QuotaAllocationId, second.QuotaAllocationId);
        Assert.Equal(me.UserId, first.UserId);
        Assert.Equal(me.UserUnique, first.UserUnique);
        Assert.Equal("Ada Lovelace", first.UserDisplayName);
        Assert.Equal(me.Email, first.UserEmail);
        Assert.Equal((Period.Year, Period.Month), (first.PeriodYear, first.PeriodMonth));
        Assert.False(first.IsUnlimited);
        Assert.Equal(TestGatewayTiers.PowerCap, first.AllocatedTokens);
        Assert.Equal(0, first.TokensUsed);
        Assert.Equal(0d, first.PercentUsed);
        Assert.Equal(QuotaLevelType.UserOverride, first.ResolvedLevelType);
        Assert.Equal(GatewayTiers.Power, first.TierProductId);
        Assert.False(first.IsGatewayCapped);
        Assert.Null(first.ResetDate); // on-demand rows are not "reset" rows
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync(a => a.UserId == me.UserId)); // persisted, once
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>()); // derived state, not an audited action
    }

    [Fact]
    public async Task GetMyAllocationAsync_follows_the_clock_into_a_new_period()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var service = CreateService(me.EntraObjectId);

        var september = await service.GetMyAllocationAsync(CancellationToken.None);
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 1, 0, 0, 1, TimeSpan.Zero));
        var october = await service.GetMyAllocationAsync(CancellationToken.None);

        Assert.NotEqual(september.QuotaAllocationId, october.QuotaAllocationId);
        Assert.Equal((2026, 10), (october.PeriodYear, october.PeriodMonth));
    }

    [Fact]
    public async Task GetMyAllocationAsync_returns_unlimited_shape_with_null_percent()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.IsUnlimited = true);

        var result = await CreateService(me.EntraObjectId).GetMyAllocationAsync(CancellationToken.None);

        Assert.True(result.IsUnlimited);
        Assert.Null(result.AllocatedTokens);
        Assert.Null(result.PercentUsed);
        Assert.Equal(GatewayTiers.Unlimited, result.TierProductId);
    }

    [Fact]
    public async Task GetMyAllocationAsync_throws_UnauthorizedAccessException_for_a_caller_with_no_User_row()
    {
        await SeedReferenceDataAsync();

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CreateService("no-such-oid").GetMyAllocationAsync(CancellationToken.None));

        Assert.Contains("GET /users/me", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMyAllocationAsync_throws_UnauthorizedAccessException_for_a_deactivated_user_and_creates_nothing()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Gone", u =>
        {
            u.IsActive = false;
            u.ApimSubscriptionId = "sub-gone";
        });

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CreateService(me.EntraObjectId).GetMyAllocationAsync(CancellationToken.None));

        Assert.Contains("deactivated", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"POST /users/{me.UserId}/activate", exception.Message, StringComparison.Ordinal);
        Assert.False(await Context.QuotaAllocations.AsNoTracking().AnyAsync(a => a.UserId == me.UserId));
        Assert.Empty(_tierSync.Calls); // and no tier sync onto a product either
    }

    // -- GetUserAllocationAsync --

    [Fact]
    public async Task GetUserAllocationAsync_returns_the_row_with_computed_percent()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var dev = await SeedUserAsync("Dev", u => u.MonthlyTokenQuota = 1_000);
        await SeedAllocationAsync(dev, Period, allocated: 1_000, tokensUsed: 250);

        var result = await CreateService(admin.EntraObjectId).GetUserAllocationAsync(dev.UserId, CancellationToken.None);

        Assert.Equal(dev.UserId, result.UserId);
        Assert.Equal("Dev", result.UserDisplayName);
        Assert.Equal(25d, result.PercentUsed);
    }

    [Fact]
    public async Task GetUserAllocationAsync_reports_a_zero_quota_as_fully_used_the_moment_anything_is_used()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var dev = await SeedUserAsync("Dev", u => u.MonthlyTokenQuota = 0);
        await SeedAllocationAsync(dev, Period, allocated: 0, tokensUsed: 1);

        var result = await CreateService(admin.EntraObjectId).GetUserAllocationAsync(dev.UserId, CancellationToken.None);

        Assert.Equal(100d, result.PercentUsed);
    }

    [Fact]
    public async Task GetUserAllocationAsync_is_read_only_a_user_without_a_row_this_period_is_404_with_guidance()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var dev = await SeedUserAsync("Dev");
        await SeedAllocationAsync(dev, new BillingPeriod(2026, 8), allocated: 1, tokensUsed: 0); // last month only

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateService(admin.EntraObjectId).GetUserAllocationAsync(dev.UserId, CancellationToken.None));

        Assert.Contains("no quota allocation for 2026-09", exception.Message, StringComparison.Ordinal);
        Assert.Contains("POST /quota/reset", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync(a => a.UserId == dev.UserId)); // nothing created
    }

    [Fact]
    public async Task GetUserAllocationAsync_unknown_user_is_404()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateService(admin.EntraObjectId).GetUserAllocationAsync(999_999, CancellationToken.None));

        Assert.Contains("999999", exception.Message, StringComparison.Ordinal);
    }

    // -- ListCurrentPeriodAsync --

    [Fact]
    public async Task ListCurrentPeriodAsync_returns_only_this_periods_rows_ordered_by_display_name_with_paging()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var zed = await SeedUserAsync("Zed");
        var amy = await SeedUserAsync("Amy");
        var max = await SeedUserAsync("Max");
        await SeedAllocationAsync(zed, Period, allocated: 1, tokensUsed: 0);
        await SeedAllocationAsync(amy, Period, allocated: 1, tokensUsed: 0);
        await SeedAllocationAsync(max, Period, allocated: 1, tokensUsed: 0);
        await SeedAllocationAsync(amy, new BillingPeriod(2026, 8), allocated: 1, tokensUsed: 0); // not current

        var service = CreateService(admin.EntraObjectId);
        var page1 = await service.ListCurrentPeriodAsync(NoFilter, new PagedRequest(Page: 1, PageSize: 2), CancellationToken.None);
        var page2 = await service.ListCurrentPeriodAsync(NoFilter, new PagedRequest(Page: 2, PageSize: 2), CancellationToken.None);

        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(["Amy", "Max"], page1.Items.Select(i => i.UserDisplayName));
        Assert.Equal(["Zed"], page2.Items.Select(i => i.UserDisplayName));
        Assert.All(page1.Items.Concat(page2.Items), i => Assert.Equal((Period.Year, Period.Month), (i.PeriodYear, i.PeriodMonth)));
    }

    // -- Estimated cost (#177) --

    [Fact]
    public async Task An_allocation_carries_no_estimated_cost_until_a_rate_card_is_configured()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var dev = await SeedUserAsync("Dev", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 2_000_000);

        var result = await CreateService(admin.EntraObjectId).GetUserAllocationAsync(dev.UserId, CancellationToken.None);

        Assert.Null(result.EstimatedCost);
    }

    [Fact]
    public async Task An_allocation_is_priced_at_the_blended_rate_once_one_is_configured()
    {
        _costEstimator.RateCard = FixedCostEstimator.Blended(10m);
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var dev = await SeedUserAsync("Dev", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        await SeedAllocationAsync(dev, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 2_000_000);

        var result = await CreateService(admin.EntraObjectId).GetUserAllocationAsync(dev.UserId, CancellationToken.None);

        Assert.Equal(20m, result.EstimatedCost);
    }

    // -- ResetAsync --

    [Fact]
    public async Task ResetAsync_resolves_every_active_user_creates_missing_rows_preserves_TokensUsed_clears_IsHardStopped_and_audits_once()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin", u => u.IsUnlimited = true);
        var fresh = await SeedUserAsync("Fresh", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var midMonth = await SeedUserAsync("MidMonth", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);
        var inactive = await SeedUserAsync("Gone", u => u.IsActive = false);
        await SeedAllocationAsync(midMonth, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 777, isHardStopped: true);
        var service = CreateService(admin.EntraObjectId);

        var result = await service.ResetAsync(CancellationToken.None);

        Assert.Equal(3, result.UsersResetCount); // admin, fresh, midMonth — not the inactive user
        Assert.Equal((Period.Year, Period.Month, Now), (result.PeriodYear, result.PeriodMonth, result.ResetDate));

        var rows = await Context.QuotaAllocations.AsNoTracking().Where(a => a.PeriodYear == Period.Year && a.PeriodMonth == Period.Month).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, r => r.UserId == inactive.UserId);
        Assert.All(rows, r => Assert.Equal(Now, r.ResetDate));
        Assert.All(rows, r => Assert.False(r.IsHardStopped));

        var freshRow = rows.Single(r => r.UserId == fresh.UserId);
        Assert.Equal(0, freshRow.TokensUsed);
        Assert.Equal(TestGatewayTiers.StandardCap, freshRow.AllocatedTokens);

        var midMonthRow = rows.Single(r => r.UserId == midMonth.UserId);
        Assert.Equal(777, midMonthRow.TokensUsed); // preserved: the gateway window, not the reset, zeroes usage
        Assert.Equal(TestGatewayTiers.PowerCap, midMonthRow.AllocatedTokens); // re-resolved

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.QuotaAllocationReset).ToListAsync());
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.Equal(string.Empty, audit.TargetType);
        Assert.Equal(Now, audit.OccurredDate);
        using var details = JsonDocument.Parse(audit.Details);
        Assert.Equal(3, details.RootElement.GetProperty("usersResetCount").GetInt32());
        Assert.Equal(Period.Year, details.RootElement.GetProperty("periodYear").GetInt32());
        Assert.Equal(Period.Month, details.RootElement.GetProperty("periodMonth").GetInt32());
    }

    [Fact]
    public async Task ResetAsync_is_idempotent_a_second_run_adds_no_rows_keeps_TokensUsed_and_audits_again()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var dev = await SeedUserAsync("Dev", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var service = CreateService(admin.EntraObjectId);

        var first = await service.ResetAsync(CancellationToken.None);

        // Reconciliation lands some usage between the two runs.
        var row = await Context.QuotaAllocations.SingleAsync(a => a.UserId == dev.UserId);
        row.TokensUsed = 4_242;
        await Context.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromHours(1));
        var second = await service.ResetAsync(CancellationToken.None);

        Assert.Equal(first.UsersResetCount, second.UsersResetCount);
        Assert.Equal(2, await Context.QuotaAllocations.AsNoTracking().CountAsync()); // admin + dev, still
        var devRow = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == dev.UserId);
        Assert.Equal(4_242, devRow.TokensUsed);
        Assert.Equal(Now.AddHours(1), devRow.ResetDate); // stamped by the latest run
        Assert.Equal(2, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.QuotaAllocationReset)); // one per run
    }

    [Fact]
    public async Task ResetAsync_that_loses_a_race_to_a_concurrent_writer_adopts_the_winning_rows_and_still_commits_once()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var racer = await SeedUserAsync("Racer", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);
        var calm = await SeedUserAsync("Calm", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);

        // Between our resolution and our save, "someone else" (a developer's first /me, another admin's
        // reset) inserts Racer's row for this period with usage already reconciled onto it.
        var raceInserter = new RacingResolutionService(
            new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance),
            Context.Database.GetConnectionString()!,
            racer.UserId,
            Period);
        var service = CreateService(admin.EntraObjectId, raceInserter);

        var result = await service.ResetAsync(CancellationToken.None);

        Assert.Equal(3, result.UsersResetCount);
        var rows = await Context.QuotaAllocations.AsNoTracking().Where(a => a.PeriodYear == Period.Year && a.PeriodMonth == Period.Month).ToListAsync();
        Assert.Equal(3, rows.Count); // no duplicate for Racer, no bare 500
        var racerRow = rows.Single(r => r.UserId == racer.UserId);
        Assert.Equal(raceInserter.InsertedAllocationId, racerRow.QuotaAllocationId); // the winner's row was adopted…
        Assert.Equal(999, racerRow.TokensUsed); // …with its usage intact…
        Assert.Equal(TestGatewayTiers.PowerCap, racerRow.AllocatedTokens); // …and our resolution applied
        Assert.Equal(GatewayTiers.Power, racerRow.TierProductId);
        Assert.False(racerRow.IsHardStopped);
        Assert.Equal(Now, racerRow.ResetDate);
        Assert.Equal(Now, rows.Single(r => r.UserId == calm.UserId).ResetDate);
        Assert.Equal(1, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.QuotaAllocationReset)); // still exactly one
    }

    [Fact]
    public async Task ResetAsync_with_no_active_users_still_audits_a_zero_count_run()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin", u => u.IsActive = false); // the only user, deactivated
        var service = CreateService(admin.EntraObjectId);

        var result = await service.ResetAsync(CancellationToken.None);

        Assert.Equal(0, result.UsersResetCount);
        Assert.Equal(1, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.QuotaAllocationReset));
    }

    [Fact]
    public async Task ResetAsync_invokes_tier_sync_for_subscribed_users_whose_tier_changed()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var dev = await SeedUserAsync("Dev", u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
            u.ApimSubscriptionId = "sub-dev";
        });
        await SeedAllocationAsync(dev, Period, allocated: 1, tokensUsed: 0, tier: GatewayTiers.Standard);

        _ = await CreateService(admin.EntraObjectId).ResetAsync(CancellationToken.None);

        Assert.Equal([(dev.UserId, GatewayTiers.Power)], _tierSync.Calls);
    }

    // -- Commit point: a tier the gateway has already moved must be written down (#163) ------------

    [Fact]
    public async Task A_reset_whose_client_disconnects_the_instant_a_tier_moves_still_commits_every_row_and_its_audit()
    {
        // #163: ResetAsync audited and saved on the request's token, so an admin whose browser abandoned
        // POST /quota/reset could move a fork's worth of APIM subscriptions between tier products with
        // no allocation rows and no audit row to show for it.
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Ada Admin");
        var dev = await SeedUserAsync("Moving Dev", u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
            u.ApimSubscriptionId = "sub-dev";
        });
        await SeedAllocationAsync(dev, Period, allocated: 1, tokensUsed: 5, tier: GatewayTiers.Standard);

        using var cts = new CancellationTokenSource();
        _tierSync.AfterSync = cts.Cancel;

        _ = await CreateService(admin.EntraObjectId).ResetAsync(cts.Token);

        _tierSync.AfterSync = null;
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal([(dev.UserId, GatewayTiers.Power)], _tierSync.Calls);

        Context.ChangeTracker.Clear();
        var allocation = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == dev.UserId);
        Assert.Equal(GatewayTiers.Power, allocation.TierProductId);
        Assert.Equal(TestGatewayTiers.PowerCap, allocation.AllocatedTokens);
        Assert.Equal(Now, allocation.ResetDate);
        _ = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.QuotaAllocationReset).ToListAsync());
    }

    [Fact]
    public async Task A_first_allocation_of_the_month_whose_client_disconnects_as_the_tier_moves_still_commits_the_row()
    {
        // The same rule on the developer's own path: GET /quota/allocations/me creates the row and, for
        // a user whose tier is not the one the gateway is already enforcing, moves the subscription
        // first. The row that records which tier that was must survive the disconnect (#163).
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Hangs Up", u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
            u.ApimSubscriptionId = "sub-me";
        });

        using var cts = new CancellationTokenSource();
        _tierSync.AfterSync = cts.Cancel;

        var allocation = await CreateService(me.EntraObjectId).GetMyAllocationAsync(cts.Token);

        _tierSync.AfterSync = null;
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(GatewayTiers.Power, allocation.TierProductId);

        Context.ChangeTracker.Clear();
        var saved = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == me.UserId);
        Assert.Equal(GatewayTiers.Power, saved.TierProductId);
        Assert.Equal(TestGatewayTiers.PowerCap, saved.AllocatedTokens);
    }

    [Fact]
    public async Task An_abandoned_reset_that_never_reached_the_gateway_writes_nothing()
    {
        // The other half of the rule (CommitTokenTests states it directly): an abandoned request that
        // has changed nothing outside the database should stop, not push on to a save. Nothing is
        // resolved, nothing is moved and no allocation row appears.
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Ada Admin");
        _ = await SeedUserAsync("No Gateway Key", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(admin.EntraObjectId).ResetAsync(cts.Token));

        Assert.Empty(_tierSync.Calls);
        Context.ChangeTracker.Clear();
        Assert.Empty(await Context.QuotaAllocations.AsNoTracking().ToListAsync());
        Assert.Empty(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.QuotaAllocationReset).ToListAsync());
    }

    // -- Helpers --

    /// <summary>Real accessor + real audit + real resolution + real (Core) reset over this test's context, as DI would wire them per request.</summary>
    private QuotaAllocationService CreateService(string oid, IQuotaResolutionService? resolution = null)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimConstants.Oid, oid)], "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);
        var auditWriter = new AuditWriter(Context, _clock);
        resolution ??= new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance);
        var reset = new QuotaResetService(Context, resolution, _tierSync, new QuotaRequestExpiry(Context, auditWriter, _clock, NullLogger<QuotaRequestExpiry>.Instance), auditWriter, _clock, NullLogger<QuotaResetService>.Instance);

        return new QuotaAllocationService(
            Context,
            resolution,
            reset,
            TestGatewayTiers.Mapper(),
            _costEstimator,
            accessor,
            _clock,
            NullLogger<QuotaAllocationService>.Instance);
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

    /// <summary>
    /// Wraps the real resolution service and, right after <c>ResolveManyAsync</c> has Added its rows,
    /// inserts one of them through a <em>second</em> connection to the same shared-cache SQLite database —
    /// exactly the window a concurrent reset or <c>/me</c> would hit. Hand-rolled (no mocking library).
    /// </summary>
    private sealed class RacingResolutionService(IQuotaResolutionService inner, string connectionString, int racerUserId, BillingPeriod racePeriod) : IQuotaResolutionService
    {
        public int InsertedAllocationId { get; private set; }

        public Task<QuotaResolution> ResolveAsync(int userId, BillingPeriod period, CancellationToken cancellationToken) =>
            inner.ResolveAsync(userId, period, cancellationToken);

        public Task<QuotaPreview> PreviewAsync(int userId, CancellationToken cancellationToken) =>
            inner.PreviewAsync(userId, cancellationToken);

        public async Task<IReadOnlyList<QuotaResolution>> ResolveManyAsync(IReadOnlyCollection<int> userIds, BillingPeriod period, GatewayTierSyncMode gatewaySync, CancellationToken cancellationToken)
        {
            var results = await inner.ResolveManyAsync(userIds, period, gatewaySync, cancellationToken);

            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            await using var other = new AppDbContext(options);
            var winner = new QuotaAllocation
            {
                UserId = racerUserId,
                PeriodYear = racePeriod.Year,
                PeriodMonth = racePeriod.Month,
                AllocatedTokens = 1,
                TokensUsed = 999,
                IsHardStopped = true,
                ResolvedLevelType = QuotaLevelType.SystemDefault,
                TierProductId = GatewayTiers.Standard,
            };
            other.QuotaAllocations.Add(winner);
            await other.SaveChangesAsync(cancellationToken);
            InsertedAllocationId = winner.QuotaAllocationId;

            return results;
        }
    }
}
