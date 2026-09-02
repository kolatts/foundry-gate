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
using FoundryGate.Domain.Exceptions;
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

    [Fact]
    public async Task A_concurrent_write_between_the_check_and_the_claim_loses_with_a_409()
    {
        // #170/#204: the read-then-compare is only the friendly refusal. The guard is the conditional
        // UPDATE … WHERE UpdatedDate = @expected, and this is the only way to reach it — the competing
        // write lands from a second context in the window after our pre-check has passed and before our
        // statement runs, which is exactly the race two admins with the config form open produce.
        await SeedReferenceDataAsync();
        var first = await SeedUserAsync("Ada Lovelace");
        var second = await SeedUserAsync("Grace Hopper");

        var seen = await CreateService(first.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.ResetDayOfMonth,
            new UpdateSystemConfigRequest { Value = "4" },
            CancellationToken.None);

        var raced = false;
        CommandInterceptor.BeforeExecuting = sql =>
        {
            if (raced || !sql.Contains("UPDATE \"SystemConfigurations\"", StringComparison.Ordinal))
            {
                return;
            }

            // Set before the competing write, whose own UPDATE re-enters this callback.
            raced = true;
            using var winner = CreateVerificationContext();
            _ = winner.SystemConfigurations
                .Where(c => c.Key == SystemConfigurationKeys.ResetDayOfMonth)
                .ExecuteUpdate(setters => setters
                    .SetProperty(c => c.Value, "9")
                    .SetProperty(c => c.UpdatedDate, seen.UpdatedDate.AddMinutes(1)));
        };

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            CreateService(second.EntraObjectId).UpdateAsync(
                SystemConfigurationKeys.ResetDayOfMonth,
                new UpdateSystemConfigRequest { Value = "5", ExpectedUpdatedDate = seen.UpdatedDate },
                CancellationToken.None));

        CommandInterceptor.BeforeExecuting = _ => { };
        Assert.True(raced);
        Assert.Contains(SystemConfigurationKeys.ResetDayOfMonth, exception.Message, StringComparison.Ordinal);
        Assert.Contains("'9'", exception.Message, StringComparison.Ordinal);

        // The loser wrote nothing — not the value, and not a config.updated row claiming it did. The row
        // reads "4" rather than the winner's "9" because the harness's second context shares this one's
        // connection, so the competing write joined the service's transaction and rolled back with it.
        // That is a property of the harness, not of the service: what this test proves is that the claim
        // saw a changed UpdatedDate, matched no row, and refused — which the 409 above establishes.
        await using var verification = CreateVerificationContext();
        var row = await verification.SystemConfigurations.AsNoTracking()
            .SingleAsync(c => c.Key == SystemConfigurationKeys.ResetDayOfMonth);
        Assert.Equal("4", row.Value);
        Assert.NotEqual("5", row.Value);
        Assert.Single(await verification.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.ConfigUpdated).ToListAsync());
    }

    [Fact]
    public async Task The_affected_user_query_selects_exactly_the_users_the_resolution_chain_puts_on_the_system_default()
    {
        // #204 review: SystemDefaultUserIdsAsync re-expresses levels 1-4 of the precedence chain as a SQL
        // predicate, while QuotaResolutionService.ResolveLevelAsync owns the same rule in C#. They agree
        // today and nothing else keeps them agreeing — add a level, or change "any unlimited group wins",
        // and both halves would still return a plausible list. One user per level, none with an existing
        // allocation (so the union's stale-row half is empty and this compares the predicates alone).
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var userUnlimited = await SeedUserAsync("User unlimited", u => u.IsUnlimited = true);
        var userOverride = await SeedUserAsync("User override", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);
        var groupUnlimited = await SeedUserAsync("Group unlimited");
        var groupMax = await SeedUserAsync("Group max");
        var systemDefault = await SeedUserAsync("System default");
        var deactivated = await SeedUserAsync("Deactivated", u => u.IsActive = false);
        await SeedGroupMembershipAsync(groupUnlimited, groupQuota: null, isUnlimited: true);
        await SeedGroupMembershipAsync(groupMax, groupQuota: TestGatewayTiers.PowerCap);

        // What the chain itself says, asked user by user through the service that owns the rule.
        var resolution = new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance);
        var byTheChain = new List<int>();
        foreach (var user in new[] { admin, userUnlimited, userOverride, groupUnlimited, groupMax, systemDefault, deactivated })
        {
            if ((await resolution.PreviewAsync(user.UserId, CancellationToken.None)).Level == QuotaLevelType.SystemDefault
                && user.IsActive)
            {
                byTheChain.Add(user.UserId);
            }
        }

        Assert.Equal([admin.UserId, systemDefault.UserId], byTheChain);

        _ = await CreateService(admin.EntraObjectId).UpdateAsync(
            SystemConfigurationKeys.DefaultMonthlyTokenQuota,
            new UpdateSystemConfigRequest { Value = TestGatewayTiers.PowerCap.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            CancellationToken.None);

        // What the SQL predicate picked: exactly the users that now hold a re-resolved allocation.
        await using var verification = CreateVerificationContext();
        var byTheQuery = await verification.QuotaAllocations.AsNoTracking()
            .OrderBy(a => a.UserId)
            .Select(a => a.UserId)
            .ToListAsync();

        Assert.Equal(byTheChain, byTheQuery);
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
