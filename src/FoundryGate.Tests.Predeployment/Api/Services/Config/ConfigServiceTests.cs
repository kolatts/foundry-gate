using System.Security.Claims;
using System.Text.Json;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Config;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Quota;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Config;

/// <summary>
/// What a <c>PUT /config</c> on <c>DefaultMonthlyTokenQuota</c> does to the developers it governs
/// (#193): level 5 of the precedence chain moves, so everyone who falls through to it is re-resolved
/// for the current period, in the same unit of work, and their APIM subscription follows.
/// </summary>
/// <remarks>
/// The HTTP surface of <c>/config</c> (auth matrix, per-key validation, the #170 concurrency check) is
/// covered end-to-end by <c>Api/Endpoints/ConfigEndpointTests</c>; this class is about the resolution
/// side, which needs a recording tier sync to observe.
/// </remarks>
public class ConfigServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BillingPeriod Period = new(2026, 9);

    private readonly MutableTimeProvider _clock = new(Now);
    private readonly RecordingGatewayTierSync _tierSync = new();

    [Fact]
    public async Task Raising_the_default_moves_every_default_tier_users_allocation_and_their_gateway_tier()
    {
        await SeedReferenceDataAsync(); // DefaultMonthlyTokenQuota = 5,000,000 (Standard)
        var admin = await SeedUserAsync("Admin");
        var falls = await SeedUserAsync("Falls through", u => u.ApimSubscriptionId = "sub-falls");
        var alsoFalls = await SeedUserAsync("Also falls through", u => u.ApimSubscriptionId = "sub-also");
        await SeedAllocationAsync(falls, TestGatewayTiers.StandardCap, QuotaLevelType.SystemDefault, GatewayTiers.Standard);
        await SeedAllocationAsync(alsoFalls, TestGatewayTiers.StandardCap, QuotaLevelType.SystemDefault, GatewayTiers.Standard);

        var response = await CreateService(admin.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            new UpdateSystemConfigRequest { Value = TestGatewayTiers.PowerCap.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CancellationToken.None);

        Assert.Equal(TestGatewayTiers.PowerCap.ToString(System.Globalization.CultureInfo.InvariantCulture), response.Value);

        // The allocations moved with the config row, in one save.
        await using var verification = CreateVerificationContext();
        var rows = await verification.QuotaAllocations.AsNoTracking().ToDictionaryAsync(a => a.UserId);
        Assert.Equal(TestGatewayTiers.PowerCap, rows[falls.UserId].AllocatedTokens);
        Assert.Equal(GatewayTiers.Power, rows[falls.UserId].TierProductId);
        Assert.Equal(QuotaLevelType.SystemDefault, rows[falls.UserId].ResolvedLevelType);
        Assert.Equal(GatewayTiers.Power, rows[alsoFalls.UserId].TierProductId);

        // And so did the gateway, once per developer whose tier actually changed.
        Assert.Equal(
            [(falls.UserId, GatewayTiers.Power), (alsoFalls.UserId, GatewayTiers.Power)],
            _tierSync.Calls.OrderBy(call => call.UserId).ToList());
    }

    [Fact]
    public async Task A_user_or_group_override_is_untouched_and_never_reaches_the_gateway()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var pinned = await SeedUserAsync("Pinned", u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
            u.ApimSubscriptionId = "sub-pinned";
        });
        var grouped = await SeedUserAsync("Grouped", u => u.ApimSubscriptionId = "sub-grouped");
        await SeedGroupMembershipAsync(grouped, groupQuota: TestGatewayTiers.StandardCap);
        await SeedAllocationAsync(pinned, TestGatewayTiers.StandardCap, QuotaLevelType.UserOverride, GatewayTiers.Standard);
        await SeedAllocationAsync(grouped, TestGatewayTiers.StandardCap, QuotaLevelType.GroupMax, GatewayTiers.Standard);

        _ = await CreateService(admin.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            new UpdateSystemConfigRequest { Value = TestGatewayTiers.PowerCap.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CancellationToken.None);

        // Neither user resolves to the system default, so neither is in the affected set at all.
        await using var verification = CreateVerificationContext();
        var rows = await verification.QuotaAllocations.AsNoTracking().ToDictionaryAsync(a => a.UserId);
        Assert.Equal(GatewayTiers.Standard, rows[pinned.UserId].TierProductId);
        Assert.Equal(GatewayTiers.Standard, rows[grouped.UserId].TierProductId);
        Assert.Empty(_tierSync.Calls);
    }

    [Fact]
    public async Task A_deactivated_user_is_not_re_resolved()
    {
        // Their key is revoked, so there is no subscription to move and no budget to correct — the same
        // rule the monthly reset and GroupService apply.
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var departed = await SeedUserAsync("Departed", u =>
        {
            u.IsActive = false;
            u.ApimSubscriptionId = "sub-departed";
        });
        await SeedAllocationAsync(departed, TestGatewayTiers.StandardCap, QuotaLevelType.SystemDefault, GatewayTiers.Standard);

        _ = await CreateService(admin.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            new UpdateSystemConfigRequest { Value = TestGatewayTiers.PowerCap.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CancellationToken.None);

        await using var verification = CreateVerificationContext();
        var row = await verification.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == departed.UserId);
        Assert.Equal(GatewayTiers.Standard, row.TierProductId);
        Assert.Empty(_tierSync.Calls);
    }

    [Fact]
    public async Task The_audit_row_records_how_many_developers_the_edit_moved()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var withKey = await SeedUserAsync("With a key", u => u.ApimSubscriptionId = "sub-with");
        var withoutKey = await SeedUserAsync("Without a key");
        await SeedAllocationAsync(withKey, TestGatewayTiers.StandardCap, QuotaLevelType.SystemDefault, GatewayTiers.Standard);
        await SeedAllocationAsync(withoutKey, TestGatewayTiers.StandardCap, QuotaLevelType.SystemDefault, GatewayTiers.Standard);

        _ = await CreateService(admin.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            new UpdateSystemConfigRequest { Value = TestGatewayTiers.PowerCap.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CancellationToken.None);

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.ConfigUpdated).ToListAsync());
        using var details = JsonDocument.Parse(audit.Details);

        // Three, not two: the acting admin is an active user with no override of their own, so they fall
        // through to the system default like anyone else and are re-resolved with everybody.
        Assert.Equal(3, details.RootElement.GetProperty("reresolvedUserCount").GetInt32());

        // Only the developer with an APIM subscription can have a tier moved; the other one's allocation
        // is corrected in SQL and there is nothing at the gateway to change.
        Assert.Equal(1, details.RootElement.GetProperty("tierChangeCount").GetInt32());
        Assert.Equal([(withKey.UserId, GatewayTiers.Power)], _tierSync.Calls);
    }

    [Fact]
    public async Task Re_saving_the_same_default_re_resolves_nobody()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var falls = await SeedUserAsync("Falls through", u => u.ApimSubscriptionId = "sub-falls");

        // Same value the seed already carries. The row is still stamped (that is what the explicit
        // UpdatedDate is for) but nothing else happens.
        var response = await CreateService(admin.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            new UpdateSystemConfigRequest { Value = TestGatewayTiers.StandardCap.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CancellationToken.None);

        Assert.Equal(admin.UserId, response.UpdatedByUserId);
        Assert.Empty(_tierSync.Calls);
        Assert.False(await Context.QuotaAllocations.AsNoTracking().AnyAsync(a => a.UserId == falls.UserId));

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.ConfigUpdated).ToListAsync());
        using var details = JsonDocument.Parse(audit.Details);
        Assert.Equal(0, details.RootElement.GetProperty("reresolvedUserCount").GetInt32());
    }

    [Fact]
    public async Task Editing_another_key_re_resolves_nobody()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var falls = await SeedUserAsync("Falls through", u => u.ApimSubscriptionId = "sub-falls");
        await SeedAllocationAsync(falls, TestGatewayTiers.StandardCap, QuotaLevelType.SystemDefault, GatewayTiers.Standard);

        _ = await CreateService(admin.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.ResetDayOfMonth,
            new UpdateSystemConfigRequest { Value = "7" },
            CancellationToken.None);

        Assert.Empty(_tierSync.Calls);
        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.ConfigUpdated).ToListAsync());
        using var details = JsonDocument.Parse(audit.Details);
        Assert.Equal(0, details.RootElement.GetProperty("reresolvedUserCount").GetInt32());
    }

    [Fact]
    public async Task A_default_tier_user_with_no_allocation_yet_gets_one_at_the_new_default()
    {
        // A developer who has not hit /me this month has no row. Skipping them would leave them to be
        // resolved later by something reading the OLD value out of a stale cache — and it costs one
        // insert to be right now.
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var fresh = await SeedUserAsync("Never resolved", u => u.ApimSubscriptionId = "sub-fresh");

        _ = await CreateService(admin.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            new UpdateSystemConfigRequest { Value = TestGatewayTiers.PowerCap.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CancellationToken.None);

        await using var verification = CreateVerificationContext();
        var row = await verification.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == fresh.UserId);
        Assert.Equal(TestGatewayTiers.PowerCap, row.AllocatedTokens);
        Assert.Equal(GatewayTiers.Power, row.TierProductId);
        Assert.Equal(0, row.TokensUsed);
    }

    [Fact]
    public async Task A_failed_gateway_move_fails_the_edit_and_the_config_row_is_unchanged()
    {
        // The whole point of doing this in one unit of work: the database must never claim a default the
        // gateway refused to enforce.
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var falls = await SeedUserAsync("Falls through", u => u.ApimSubscriptionId = "sub-falls");
        await SeedAllocationAsync(falls, TestGatewayTiers.StandardCap, QuotaLevelType.SystemDefault, GatewayTiers.Standard);
        _tierSync.ThrowFor = falls.UserId;

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(admin.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            new UpdateSystemConfigRequest { Value = TestGatewayTiers.PowerCap.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CancellationToken.None));

        await using var verification = CreateVerificationContext();
        var config = await verification.SystemConfigurations.AsNoTracking()
            .SingleAsync(c => c.Key == SystemConfigurationKeys.DefaultMonthlyTokenQuota);
        Assert.Equal(TestGatewayTiers.StandardCap.ToString(System.Globalization.CultureInfo.InvariantCulture), config.Value);
        Assert.Empty(await verification.AuditLogs.AsNoTracking().ToListAsync());
    }

    // -- Helpers --

    private ConfigService CreateService(string oid)
    {
        List<Claim> claims = [new Claim(ClaimConstants.Oid, oid), new Claim(ClaimConstants.Roles, RoleNames.Admin)];
        var identity = new ClaimsIdentity(claims, "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);
        var auditWriter = new AuditWriter(Context, _clock);

        return new ConfigService(
            Context,
            new SystemConfigValidator(TestGatewayTiers.Mapper()),
            new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance),
            accessor,
            new AuditService(Context, auditWriter, accessor),
            _clock);
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

    private async Task SeedAllocationAsync(User user, long? allocated, QuotaLevelType level, string tier)
    {
        var allocation = new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = Period.Year,
            PeriodMonth = Period.Month,
            AllocatedTokens = allocated,
            TokensUsed = 0,
            ResolvedLevelType = level,
            TierProductId = tier,
        };
        Context.QuotaAllocations.Add(allocation);
        await Context.SaveChangesAsync();
        Context.Entry(allocation).State = EntityState.Detached;
    }

    private async Task SeedGroupMembershipAsync(User user, long? groupQuota, bool isUnlimited = false)
    {
        var group = new Group
        {
            Name = $"g-{Guid.NewGuid():N}",
            MonthlyTokenQuota = groupQuota,
            IsUnlimited = isUnlimited,
        };
        Context.Groups.Add(group);
        await Context.SaveChangesAsync();

        Context.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = user.UserId });
        await Context.SaveChangesAsync();
    }
}
