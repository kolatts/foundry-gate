using System.Security.Claims;
using System.Text.Json;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Groups;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Quota;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Quota;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Groups;

/// <summary>
/// <see cref="EntraGroupSyncService"/> (#41) against a <see cref="FakeEntraDirectoryClient"/> on a real
/// SQLite <c>AppDbContext</c>: the add/remove reconciliation, idempotency, orphan members being skipped
/// <em>and counted</em>, a directory page far larger than Graph's 100-item page, the quota re-resolution
/// every membership change triggers, and the single audit row per group.
/// </summary>
public class EntraGroupSyncServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BillingPeriod Period = new(2026, 9);

    private readonly MutableTimeProvider _clock = new(Now);
    private readonly RecordingGatewayTierSync _tierSync = new();
    private readonly FakeEntraDirectoryClient _directory = new();

    [Fact]
    public async Task SyncAsync_adds_the_directorys_members_as_system_memberships_and_reresolves_them()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var group = await SeedLinkedGroupAsync("Platform", quota: TestGatewayTiers.PowerCap);
        var joiner = await SeedUserAsync("Joiner", u => u.ApimSubscriptionId = "sub-joiner");
        _directory.GroupMembers[group.EntraGroupId] = [joiner.EntraObjectId];

        var result = await CreateService(admin).SyncAsync(group.GroupId, CancellationToken.None);

        Assert.Equal(new(group.GroupId, AddedCount: 1, RemovedCount: 0, SkippedUnknownUserCount: 0), result);

        var membership = await Context.GroupMembers.AsNoTracking()
            .SingleAsync(m => m.GroupId == group.GroupId && m.UserId == joiner.UserId);
        Assert.Null(membership.AddedByUserId); // the directory is the actor, not the calling admin

        var allocation = await AllocationAsync(joiner.UserId);
        Assert.Equal(TestGatewayTiers.PowerCap, allocation.AllocatedTokens);
        Assert.Equal(QuotaLevelType.GroupMax, allocation.ResolvedLevelType);
        Assert.Equal([(joiner.UserId, GatewayTiers.Power)], _tierSync.Calls);
    }

    [Fact]
    public async Task SyncAsync_removes_departed_members_and_reresolves_them_down_the_chain()
    {
        await SeedReferenceDataAsync(); // system default = the Standard cap
        var admin = await SeedUserAsync("Admin");
        var group = await SeedLinkedGroupAsync("Leavers", quota: TestGatewayTiers.PowerCap);
        var staying = await SeedUserAsync("Staying");
        var leaving = await SeedUserAsync("Leaving", u => u.ApimSubscriptionId = "sub-leaving");
        await SeedMembershipAsync(group, staying, leaving);
        await SeedAllocationAsync(leaving, TestGatewayTiers.PowerCap, GatewayTiers.Power);
        _directory.GroupMembers[group.EntraGroupId] = [staying.EntraObjectId];
        _tierSync.Calls.Clear();

        var result = await CreateService(admin).SyncAsync(group.GroupId, CancellationToken.None);

        Assert.Equal(new(group.GroupId, AddedCount: 0, RemovedCount: 1, SkippedUnknownUserCount: 0), result);
        Assert.True(await Context.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == group.GroupId && m.UserId == staying.UserId));
        Assert.False(await Context.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == group.GroupId && m.UserId == leaving.UserId));

        var allocation = await AllocationAsync(leaving.UserId);
        Assert.Equal(TestGatewayTiers.StandardCap, allocation.AllocatedTokens);
        Assert.Equal(QuotaLevelType.SystemDefault, allocation.ResolvedLevelType);
        Assert.Equal([(leaving.UserId, GatewayTiers.Standard)], _tierSync.Calls);
    }

    [Fact]
    public async Task SyncAsync_is_idempotent_a_second_run_with_an_unchanged_directory_changes_nothing()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var group = await SeedLinkedGroupAsync("Steady", quota: TestGatewayTiers.PowerCap);
        var member = await SeedUserAsync("Member", u => u.ApimSubscriptionId = "sub-member");
        _directory.GroupMembers[group.EntraGroupId] = [member.EntraObjectId];
        var service = CreateService(admin);

        var first = await service.SyncAsync(group.GroupId, CancellationToken.None);
        _tierSync.Calls.Clear();
        var second = await service.SyncAsync(group.GroupId, CancellationToken.None);

        Assert.Equal(1, first.AddedCount);
        Assert.Equal(new(group.GroupId, AddedCount: 0, RemovedCount: 0, SkippedUnknownUserCount: 0), second);
        Assert.Empty(_tierSync.Calls);
        _ = Assert.Single(await Context.GroupMembers.AsNoTracking().Where(m => m.GroupId == group.GroupId).ToListAsync());
        Assert.Equal(2, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.GroupEntraSynced));
    }

    [Fact]
    public async Task SyncAsync_skips_and_counts_directory_members_with_no_FoundryGate_user()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var group = await SeedLinkedGroupAsync("Orphans", quota: TestGatewayTiers.PowerCap);
        var known = await SeedUserAsync("Known");
        _directory.GroupMembers[group.EntraGroupId] =
            [known.EntraObjectId, Guid.NewGuid().ToString(), Guid.NewGuid().ToString()];

        var result = await CreateService(admin).SyncAsync(group.GroupId, CancellationToken.None);

        Assert.Equal(new(group.GroupId, AddedCount: 1, RemovedCount: 0, SkippedUnknownUserCount: 2), result);
        _ = Assert.Single(await Context.GroupMembers.AsNoTracking().Where(m => m.GroupId == group.GroupId).ToListAsync());

        var audit = await SingleSyncAuditAsync();
        var details = JsonDocument.Parse(audit.Details).RootElement;
        Assert.Equal(2, details.GetProperty("skippedUnknownUserCount").GetInt32());
        Assert.Equal(3, details.GetProperty("directoryMemberCount").GetInt32());
    }

    [Fact]
    public async Task SyncAsync_handles_a_directory_page_far_larger_than_Graphs_100_item_page()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var group = await SeedLinkedGroupAsync("Big", quota: TestGatewayTiers.PowerCap);

        var oids = new List<string>();
        for (var i = 0; i < 250; i++)
        {
            var user = await SeedUserAsync($"Dev {i:D3}");
            oids.Add(user.EntraObjectId);
        }

        // Duplicated on purpose: Graph can list a principal on more than one page, and a duplicate must
        // not become a second membership row (the composite PK would reject it anyway).
        _directory.GroupMembers[group.EntraGroupId] = [.. oids, oids[0], oids[^1]];

        var result = await CreateService(admin).SyncAsync(group.GroupId, CancellationToken.None);

        Assert.Equal(250, result.AddedCount);
        Assert.Equal(250, await Context.GroupMembers.AsNoTracking().CountAsync(m => m.GroupId == group.GroupId));
        Assert.Equal(250, await Context.QuotaAllocations.AsNoTracking().CountAsync(a => a.AllocatedTokens == TestGatewayTiers.PowerCap));
    }

    [Fact]
    public async Task SyncAsync_leaves_deactivated_members_without_an_allocation()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var group = await SeedLinkedGroupAsync("Mixed", quota: TestGatewayTiers.PowerCap);
        var active = await SeedUserAsync("Active");
        var inactive = await SeedUserAsync("Inactive", u => u.IsActive = false);
        _directory.GroupMembers[group.EntraGroupId] = [active.EntraObjectId, inactive.EntraObjectId];

        var result = await CreateService(admin).SyncAsync(group.GroupId, CancellationToken.None);

        // The membership is a fact about the directory and is recorded for both…
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(2, await Context.GroupMembers.AsNoTracking().CountAsync(m => m.GroupId == group.GroupId));

        // …but only the active user gets an allocation.
        Assert.True(await Context.QuotaAllocations.AsNoTracking().AnyAsync(a => a.UserId == active.UserId));
        Assert.False(await Context.QuotaAllocations.AsNoTracking().AnyAsync(a => a.UserId == inactive.UserId));
    }

    [Fact]
    public async Task SyncAsync_400s_for_a_group_with_no_Entra_link_and_404s_for_an_unknown_group()
    {
        var admin = await SeedUserAsync("Admin");
        var unlinked = new Group { Name = "Native" };
        _ = Context.Groups.Add(unlinked);
        _ = await Context.SaveChangesAsync();
        var service = CreateService(admin);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SyncAsync(unlinked.GroupId, CancellationToken.None));
        Assert.Contains("not linked to an Entra group", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", exception.Message, StringComparison.Ordinal);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.SyncAsync(unlinked.GroupId + 9999, CancellationToken.None));
    }

    [Fact]
    public async Task SyncAsync_on_a_host_with_Entra_disabled_surfaces_the_setting_name()
    {
        var admin = await SeedUserAsync("Admin");
        var group = await SeedLinkedGroupAsync("Linked", quota: null);

        var exception = await Assert.ThrowsAsync<FeatureNotConfiguredException>(
            () => CreateService(admin, new DisabledEntraDirectoryClient()).SyncAsync(group.GroupId, CancellationToken.None));

        Assert.Contains("Entra:Enabled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_refuses_before_reading_the_directory_when_the_caller_has_no_user_row()
    {
        await SeedReferenceDataAsync();
        var group = await SeedLinkedGroupAsync("Unprovisioned", quota: TestGatewayTiers.PowerCap);
        var member = await SeedUserAsync("Member");
        _directory.GroupMembers[group.EntraGroupId] = [member.EntraObjectId];

        // An admin who has never called GET /users/me. The 403 must land before the reconciliation,
        // not out of the audit writer after memberships and APIM products have already moved.
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => CreateServiceForOid(Guid.NewGuid().ToString()).SyncAsync(group.GroupId, CancellationToken.None));

        Assert.Contains("GET /users/me", exception.Message, StringComparison.Ordinal);
        Assert.False(await Context.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == group.GroupId));
        Assert.Empty(_tierSync.Calls);
    }

    [Fact]
    public async Task SyncAllAsync_reconciles_only_the_linked_groups_one_audit_row_each()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var linkedA = await SeedLinkedGroupAsync("Linked A", quota: TestGatewayTiers.PowerCap);
        var linkedB = await SeedLinkedGroupAsync("Linked B", quota: null);
        var native = new Group { Name = "Native" };
        _ = Context.Groups.Add(native);
        _ = await Context.SaveChangesAsync();

        var member = await SeedUserAsync("Member");
        _directory.GroupMembers[linkedA.EntraGroupId] = [member.EntraObjectId];
        _directory.GroupMembers[linkedB.EntraGroupId] = [];

        var results = await CreateService(admin).SyncAllAsync(CancellationToken.None);

        Assert.Equal([linkedA.GroupId, linkedB.GroupId], results.Select(r => r.GroupId));
        Assert.Equal(1, results[0].AddedCount);
        Assert.Equal(0, results[1].AddedCount);
        Assert.Equal(2, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.GroupEntraSynced));
        Assert.False(await Context.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == AuditActions.GroupEntraSynced && a.TargetId == native.GroupId.ToString()));
    }

    // -- Helpers --

    private EntraGroupSyncService CreateService(User actor, IEntraDirectoryClient? directory = null) =>
        CreateServiceForOid(actor.EntraObjectId, directory);

    private EntraGroupSyncService CreateServiceForOid(string oid, IEntraDirectoryClient? directory = null)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimConstants.Oid, oid)], "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);

        return new EntraGroupSyncService(
            Context,
            directory ?? _directory,
            new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance),
            accessor,
            new AuditService(Context, new AuditWriter(Context, _clock), accessor),
            _clock,
            NullLogger<EntraGroupSyncService>.Instance);
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
        _ = Context.Users.Add(user);
        _ = await Context.SaveChangesAsync();
        return user;
    }

    private async Task<Group> SeedLinkedGroupAsync(string name, long? quota)
    {
        var group = new Group
        {
            Name = name,
            EntraGroupId = Guid.NewGuid().ToString(),
            MonthlyTokenQuota = quota,
        };
        _ = Context.Groups.Add(group);
        _ = await Context.SaveChangesAsync();
        return group;
    }

    private async Task SeedMembershipAsync(Group group, params User[] users)
    {
        Context.GroupMembers.AddRange(users.Select(u => new GroupMember { GroupId = group.GroupId, UserId = u.UserId }));
        _ = await Context.SaveChangesAsync();
    }

    private async Task SeedAllocationAsync(User user, long? allocated, string tierProductId)
    {
        _ = Context.QuotaAllocations.Add(new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = Period.Year,
            PeriodMonth = Period.Month,
            AllocatedTokens = allocated,
            ResolvedLevelType = QuotaLevelType.GroupMax,
            TierProductId = tierProductId,
        });
        _ = await Context.SaveChangesAsync();
    }

    private Task<QuotaAllocation> AllocationAsync(int userId) =>
        Context.QuotaAllocations.AsNoTracking()
            .SingleAsync(a => a.UserId == userId && a.PeriodYear == Period.Year && a.PeriodMonth == Period.Month);

    private async Task<AuditLog> SingleSyncAuditAsync() =>
        Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.GroupEntraSynced).ToListAsync());
}
