using System.Security.Claims;
using System.Text.Json;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Groups;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Quota;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Quota;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Groups;

/// <summary>
/// <see cref="GroupService"/> (#30, #31) on a real SQLite <c>AppDbContext</c> with the real resolution
/// service, audit writer and a movable clock: name uniqueness, the tier guard, the roster reads, and —
/// the part that matters — that every quota-visible write re-resolves the right members and moves the
/// gateway tier only for the ones whose tier actually changed.
/// </summary>
public class GroupServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BillingPeriod Period = new(2026, 9);
    private static readonly PagedRequest FirstPage = new();

    private readonly MutableTimeProvider _clock = new(Now);
    private readonly RecordingGatewayTierSync _tierSync = new();

    // -- CreateAsync --

    [Fact]
    public async Task CreateAsync_persists_the_group_and_audits_it_against_its_new_id()
    {
        var admin = await SeedUserAsync("Admin");

        var created = await CreateService(admin).CreateAsync(
            new CreateGroupRequest
            {
                Name = "  Platform Team  ",
                Description = " The people who own the gateway ",
                MonthlyTokenQuota = TestGatewayTiers.PowerCap,
            },
            CancellationToken.None);

        Assert.Equal("Platform Team", created.Name);
        Assert.Equal("The people who own the gateway", created.Description);
        Assert.Null(created.EntraGroupId);
        Assert.False(created.IsEntraSynced);
        Assert.Equal(TestGatewayTiers.PowerCap, created.MonthlyTokenQuota);
        Assert.Equal(0, created.MemberCount);
        Assert.NotEqual(Guid.Empty, created.GroupUnique);

        var audit = await SingleAuditAsync(AuditActions.GroupCreated);
        Assert.Equal(AuditTargetTypes.Group, audit.TargetType);
        Assert.Equal(created.GroupId.ToString(), audit.TargetId);
        Assert.Equal(admin.UserId, audit.ActorUserId);
    }

    [Fact]
    public async Task CreateAsync_with_an_entra_group_id_links_it_for_sync()
    {
        var admin = await SeedUserAsync("Admin");
        var entraGroupId = Guid.NewGuid().ToString();

        var created = await CreateService(admin).CreateAsync(
            new CreateGroupRequest { Name = "Synced", EntraGroupId = entraGroupId },
            CancellationToken.None);

        Assert.Equal(entraGroupId, created.EntraGroupId);
        Assert.True(created.IsEntraSynced);
    }

    [Theory]
    [InlineData("Platform Team")]
    [InlineData("platform team")]
    public async Task CreateAsync_rejects_a_duplicate_name_regardless_of_case(string duplicate)
    {
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        _ = await service.CreateAsync(new CreateGroupRequest { Name = "Platform Team" }, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => service.CreateAsync(new CreateGroupRequest { Name = duplicate }, CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_maps_a_unique_index_violation_the_pre_check_could_not_see_to_409()
    {
        // The race the index exists for: a row the pre-check's query cannot see (here, one pending in
        // the same unit of work) is inserted alongside ours and IX_Groups_Name rejects the pair. The
        // 409 must come from the provider naming the index, not from a re-query — a collation the
        // service does not model would otherwise turn this into a 500.
        var admin = await SeedUserAsync("Admin");
        _ = Context.Groups.Add(new Group { Name = "Racer" });

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService(admin).CreateAsync(new CreateGroupRequest { Name = "Racer" }, CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        _ = Assert.IsType<DbUpdateException>(exception.InnerException);
    }

    [Fact]
    public async Task CreateAsync_rethrows_a_unique_index_violation_that_is_not_about_the_name()
    {
        // Same shape, different index (IX_Groups_GroupUnique): this must NOT be dressed up as a name
        // conflict, or a genuine fault would reach the caller as a misleading 409.
        var admin = await SeedUserAsync("Admin");
        var collidingUnique = Guid.NewGuid();
        _ = Context.Groups.Add(new Group { Name = "Pending one", GroupUnique = collidingUnique });
        _ = Context.Groups.Add(new Group { Name = "Pending two", GroupUnique = collidingUnique });

        _ = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreateService(admin).CreateAsync(new CreateGroupRequest { Name = "Distinct name" }, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_refuses_a_second_group_linked_to_the_same_Entra_group()
    {
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var entraGroupId = Guid.NewGuid().ToString();
        _ = await service.CreateAsync(new CreateGroupRequest { Name = "First", EntraGroupId = entraGroupId }, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => service.CreateAsync(new CreateGroupRequest { Name = "Second", EntraGroupId = entraGroupId }, CancellationToken.None));

        Assert.Contains("already linked", exception.Message, StringComparison.Ordinal);

        // …but two native groups are fine: the index is filtered on a non-empty link.
        _ = await service.CreateAsync(new CreateGroupRequest { Name = "Native one" }, CancellationToken.None);
        _ = await service.CreateAsync(new CreateGroupRequest { Name = "Native two" }, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsync_joins_an_ambient_transaction_instead_of_opening_a_nested_one()
    {
        // An orchestrating service that owns the unit of work: BeginTransactionAsync would throw here,
        // and committing our own would commit theirs (ApimKeyService's precedent).
        var admin = await SeedUserAsync("Admin");
        await using var outer = await Context.Database.BeginTransactionAsync();

        var created = await CreateService(admin).CreateAsync(new CreateGroupRequest { Name = "Ambient" }, CancellationToken.None);

        Assert.NotNull(Context.Database.CurrentTransaction); // still ours to commit
        await outer.CommitAsync();
        Assert.True(await Context.Groups.AsNoTracking().AnyAsync(g => g.GroupId == created.GroupId));
        _ = await SingleAuditAsync(AuditActions.GroupCreated);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_quota_that_is_not_a_configured_tier()
    {
        var admin = await SeedUserAsync("Admin");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => CreateService(admin).CreateAsync(
                new CreateGroupRequest { Name = "Odd budget", MonthlyTokenQuota = 1234 },
                CancellationToken.None));

        Assert.Contains("not a configured budget tier", exception.Message, StringComparison.Ordinal);
        Assert.False(await Context.Groups.AnyAsync(g => g.Name == "Odd budget"));
    }

    // -- ListAsync / GetAsync / ListMembersAsync --

    [Fact]
    public async Task ListAsync_orders_by_name_carries_the_member_count_and_filters_case_insensitively()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var zebra = await service.CreateAsync(new CreateGroupRequest { Name = "Zebra Squad" }, CancellationToken.None);
        _ = await service.CreateAsync(new CreateGroupRequest { Name = "Alpha Squad", Description = "zebra-adjacent" }, CancellationToken.None);
        var member = await SeedUserAsync("Grace Hopper");
        _ = await service.AddMemberAsync(zebra.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None);

        var all = await service.ListAsync(new GroupQuery(null), FirstPage, CancellationToken.None);
        Assert.Equal(["Alpha Squad", "Zebra Squad"], all.Items.Select(g => g.Name));
        Assert.Equal([0, 1], all.Items.Select(g => g.MemberCount));

        // "zebra" matches the Zebra group by name and the Alpha group by description.
        var searched = await service.ListAsync(new GroupQuery("ZEBRA"), FirstPage, CancellationToken.None);
        Assert.Equal(["Alpha Squad", "Zebra Squad"], searched.Items.Select(g => g.Name));

        var narrower = await service.ListAsync(new GroupQuery("alpha"), FirstPage, CancellationToken.None);
        Assert.Equal(["Alpha Squad"], narrower.Items.Select(g => g.Name));
    }

    [Fact]
    public async Task GetAsync_returns_the_roster_ordered_by_display_name_and_404s_for_an_unknown_group()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(new CreateGroupRequest { Name = "Roster" }, CancellationToken.None);
        var zoe = await SeedUserAsync("Zoe");
        var ada = await SeedUserAsync("Ada");
        _ = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = zoe.UserId }, CancellationToken.None);
        _ = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = ada.UserId }, CancellationToken.None);

        var detail = await service.GetAsync(group.GroupId, CancellationToken.None);

        Assert.Equal(2, detail.Group.MemberCount);
        Assert.Equal(["Ada", "Zoe"], detail.Members.Select(m => m.DisplayName));
        Assert.All(detail.Members, m => Assert.Equal(admin.UserId, m.AddedByUserId));

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(group.GroupId + 9999, CancellationToken.None));
    }

    [Fact]
    public async Task ListMembersAsync_pages_and_404s_for_an_unknown_group()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(new CreateGroupRequest { Name = "Paged" }, CancellationToken.None);
        foreach (var name in new[] { "Aa", "Bb", "Cc" })
        {
            var user = await SeedUserAsync(name);
            _ = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = user.UserId }, CancellationToken.None);
        }

        var page = await service.ListMembersAsync(group.GroupId, new PagedRequest(Page: 2, PageSize: 2), CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(["Cc"], page.Items.Select(m => m.DisplayName));

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.ListMembersAsync(group.GroupId + 9999, FirstPage, CancellationToken.None));
    }

    // -- UpdateAsync --

    [Fact]
    public async Task UpdateAsync_quota_change_reresolves_every_active_member_and_syncs_only_the_changed_tiers()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(
            new CreateGroupRequest { Name = "Budget Holders", MonthlyTokenQuota = TestGatewayTiers.StandardCap },
            CancellationToken.None);

        var moving = await SeedUserAsync("Moving", u => u.ApimSubscriptionId = "sub-moving");
        var alreadyThere = await SeedUserAsync("Already", u => u.ApimSubscriptionId = "sub-already");
        var keyless = await SeedUserAsync("Keyless");
        var inactive = await SeedUserAsync("Inactive", u =>
        {
            u.IsActive = false;
            u.ApimSubscriptionId = "sub-inactive";
        });
        foreach (var user in new[] { moving, alreadyThere, keyless, inactive })
        {
            _ = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = user.UserId }, CancellationToken.None);
        }

        await SetAllocationTierAsync(moving, GatewayTiers.Standard, TestGatewayTiers.StandardCap);
        await SetAllocationTierAsync(alreadyThere, GatewayTiers.Power, TestGatewayTiers.PowerCap);
        _tierSync.Calls.Clear();

        var updated = await service.UpdateAsync(
            group.GroupId,
            new UpdateGroupRequest { Name = "Budget Holders", MonthlyTokenQuota = TestGatewayTiers.PowerCap },
            CancellationToken.None);

        Assert.Equal(TestGatewayTiers.PowerCap, updated.MonthlyTokenQuota);

        // Every active member's allocation now reads the new group policy…
        foreach (var user in new[] { moving, alreadyThere, keyless })
        {
            var allocation = await AllocationAsync(user.UserId);
            Assert.Equal(TestGatewayTiers.PowerCap, allocation.AllocatedTokens);
            Assert.Equal(QuotaLevelType.GroupMax, allocation.ResolvedLevelType);
            Assert.Equal(GatewayTiers.Power, allocation.TierProductId);
        }

        // …the deactivated member is left alone (no key to enforce against)…
        Assert.False(await Context.QuotaAllocations.AsNoTracking().AnyAsync(a => a.UserId == inactive.UserId));

        // …and only the subscription whose tier actually moved was touched.
        Assert.Equal([(moving.UserId, GatewayTiers.Power)], _tierSync.Calls);

        var audit = await SingleAuditAsync(AuditActions.GroupUpdated);
        var details = JsonDocument.Parse(audit.Details).RootElement;
        Assert.True(details.GetProperty("quotaChanged").GetBoolean());
        Assert.Equal(3, details.GetProperty("membersReresolvedCount").GetInt32());
        Assert.Equal(TestGatewayTiers.StandardCap, details.GetProperty("before").GetProperty("monthlyTokenQuota").GetInt64());
        Assert.Equal(TestGatewayTiers.PowerCap, details.GetProperty("after").GetProperty("monthlyTokenQuota").GetInt64());
    }

    [Fact]
    public async Task UpdateAsync_that_only_renames_reresolves_nobody()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(
            new CreateGroupRequest { Name = "Before", MonthlyTokenQuota = TestGatewayTiers.StandardCap },
            CancellationToken.None);
        var member = await SeedUserAsync("Member", u => u.ApimSubscriptionId = "sub-member");
        _ = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None);
        _tierSync.Calls.Clear();

        var updated = await service.UpdateAsync(
            group.GroupId,
            new UpdateGroupRequest { Name = "After", Description = "renamed", MonthlyTokenQuota = TestGatewayTiers.StandardCap },
            CancellationToken.None);

        Assert.Equal("After", updated.Name);
        Assert.Equal("renamed", updated.Description);
        Assert.Empty(_tierSync.Calls);

        var audit = await SingleAuditAsync(AuditActions.GroupUpdated);
        var details = JsonDocument.Parse(audit.Details).RootElement;
        Assert.False(details.GetProperty("quotaChanged").GetBoolean());
        Assert.Equal(0, details.GetProperty("membersReresolvedCount").GetInt32());
    }

    [Fact]
    public async Task UpdateAsync_may_keep_its_own_name_but_not_take_another_groups()
    {
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var mine = await service.CreateAsync(new CreateGroupRequest { Name = "Mine" }, CancellationToken.None);
        _ = await service.CreateAsync(new CreateGroupRequest { Name = "Yours" }, CancellationToken.None);

        var unchanged = await service.UpdateAsync(mine.GroupId, new UpdateGroupRequest { Name = "Mine" }, CancellationToken.None);
        Assert.Equal("Mine", unchanged.Name);

        _ = await Assert.ThrowsAsync<ConflictException>(
            () => service.UpdateAsync(mine.GroupId, new UpdateGroupRequest { Name = "yours" }, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_404s_for_an_unknown_group_and_rejects_a_non_tier_quota()
    {
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(new CreateGroupRequest { Name = "Real" }, CancellationToken.None);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.UpdateAsync(group.GroupId + 9999, new UpdateGroupRequest { Name = "Ghost" }, CancellationToken.None));

        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateAsync(group.GroupId, new UpdateGroupRequest { Name = "Real", MonthlyTokenQuota = 7 }, CancellationToken.None));
    }

    // -- DeleteAsync --

    [Fact]
    public async Task DeleteAsync_refuses_a_populated_group_without_force_and_changes_nothing()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(
            new CreateGroupRequest { Name = "Populated", MonthlyTokenQuota = TestGatewayTiers.PowerCap },
            CancellationToken.None);
        var member = await SeedUserAsync("Member");
        _ = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => service.DeleteAsync(group.GroupId, force: false, CancellationToken.None));

        Assert.Contains("force=true", exception.Message, StringComparison.Ordinal);
        Assert.True(await Context.Groups.AsNoTracking().AnyAsync(g => g.GroupId == group.GroupId));
        Assert.False(await Context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditActions.GroupDeleted));
    }

    [Fact]
    public async Task DeleteAsync_with_force_drops_the_memberships_and_reresolves_the_former_members_down_the_chain()
    {
        await SeedReferenceDataAsync(); // system default = the Standard cap
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(
            new CreateGroupRequest { Name = "Doomed", MonthlyTokenQuota = TestGatewayTiers.PowerCap },
            CancellationToken.None);
        var member = await SeedUserAsync("Member", u => u.ApimSubscriptionId = "sub-member");
        _ = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None);
        Assert.Equal(TestGatewayTiers.PowerCap, (await AllocationAsync(member.UserId)).AllocatedTokens);
        _tierSync.Calls.Clear();

        await service.DeleteAsync(group.GroupId, force: true, CancellationToken.None);

        Assert.False(await Context.Groups.AsNoTracking().AnyAsync(g => g.GroupId == group.GroupId));
        Assert.False(await Context.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == group.GroupId));
        Assert.True(await Context.Users.AsNoTracking().AnyAsync(u => u.UserId == member.UserId));

        // The group's Power budget is gone, so the member falls through to the system default.
        var allocation = await AllocationAsync(member.UserId);
        Assert.Equal(TestGatewayTiers.StandardCap, allocation.AllocatedTokens);
        Assert.Equal(QuotaLevelType.SystemDefault, allocation.ResolvedLevelType);
        Assert.Equal([(member.UserId, GatewayTiers.Standard)], _tierSync.Calls);

        var audit = await SingleAuditAsync(AuditActions.GroupDeleted);
        var details = JsonDocument.Parse(audit.Details).RootElement;
        Assert.True(details.GetProperty("forced").GetBoolean());
        Assert.Equal(1, details.GetProperty("removedMemberCount").GetInt32());
    }

    [Fact]
    public async Task DeleteAsync_of_an_empty_group_needs_no_force_and_404s_for_an_unknown_group()
    {
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(new CreateGroupRequest { Name = "Empty" }, CancellationToken.None);

        await service.DeleteAsync(group.GroupId, force: false, CancellationToken.None);

        Assert.False(await Context.Groups.AsNoTracking().AnyAsync(g => g.GroupId == group.GroupId));
        _ = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.DeleteAsync(group.GroupId, force: false, CancellationToken.None));
    }

    // -- Membership --

    [Fact]
    public async Task AddMemberAsync_attributes_the_membership_reresolves_the_member_and_audits()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(
            new CreateGroupRequest { Name = "Joiners", MonthlyTokenQuota = TestGatewayTiers.PowerCap },
            CancellationToken.None);
        var member = await SeedUserAsync("Joiner", u => u.ApimSubscriptionId = "sub-joiner");

        var added = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None);

        Assert.Equal(member.UserId, added.UserId);
        Assert.Equal("Joiner", added.DisplayName);
        Assert.Equal(admin.UserId, added.AddedByUserId);

        var allocation = await AllocationAsync(member.UserId);
        Assert.Equal(TestGatewayTiers.PowerCap, allocation.AllocatedTokens);
        Assert.Equal(QuotaLevelType.GroupMax, allocation.ResolvedLevelType);
        Assert.Equal([(member.UserId, GatewayTiers.Power)], _tierSync.Calls);

        var audit = await SingleAuditAsync(AuditActions.GroupMemberAdded);
        Assert.Equal(group.GroupId.ToString(), audit.TargetId);
        Assert.Equal(admin.UserId, audit.ActorUserId);
    }

    [Fact]
    public async Task AddMemberAsync_404s_for_an_unknown_group_or_user_and_409s_on_a_repeat()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(new CreateGroupRequest { Name = "Once" }, CancellationToken.None);
        var member = await SeedUserAsync("Member");

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.AddMemberAsync(group.GroupId + 9999, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None));
        _ = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId + 9999 }, CancellationToken.None));

        _ = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None);

        _ = await Assert.ThrowsAsync<ConflictException>(
            () => service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None));
    }

    [Fact]
    public async Task Membership_of_an_Entra_linked_group_cannot_be_edited_by_hand()
    {
        // The trap: the write would succeed and the next sync-entra would silently undo it.
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(
            new CreateGroupRequest { Name = "Directory owned", EntraGroupId = Guid.NewGuid().ToString() },
            CancellationToken.None);
        var user = await SeedUserAsync("Directory Person");

        var add = await Assert.ThrowsAsync<ConflictException>(
            () => service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = user.UserId }, CancellationToken.None));
        Assert.Contains("managed by Entra group", add.Message, StringComparison.Ordinal);
        Assert.Contains("sync-entra", add.Message, StringComparison.Ordinal);

        // The sync service writes the row directly, so it is unaffected by the refusal.
        _ = Context.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = user.UserId });
        _ = await Context.SaveChangesAsync();

        var remove = await Assert.ThrowsAsync<ConflictException>(
            () => service.RemoveMemberAsync(group.GroupId, user.UserId, CancellationToken.None));
        Assert.Contains("managed by Entra group", remove.Message, StringComparison.Ordinal);

        // Group policy stays editable — the directory owns the roster, not the budget.
        var renamed = await service.UpdateAsync(group.GroupId, new UpdateGroupRequest { Name = "Renamed" }, CancellationToken.None);
        Assert.Equal("Renamed", renamed.Name);
    }

    [Fact]
    public async Task Write_paths_refuse_an_unprovisioned_caller_before_re_resolving_anything()
    {
        // CONVENTIONS: resolve the actor and refuse before the call that can move APIM. Previously the
        // 403 came out of the audit writer, i.e. after ResolveManyAsync had already moved tiers.
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var setup = CreateService(admin);
        var group = await setup.CreateAsync(
            new CreateGroupRequest { Name = "Guarded", MonthlyTokenQuota = TestGatewayTiers.StandardCap },
            CancellationToken.None);
        var member = await SeedUserAsync("Member", u => u.ApimSubscriptionId = "sub-member");
        _ = await setup.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None);
        _tierSync.Calls.Clear();

        var stranger = CreateServiceForOid(Guid.NewGuid().ToString());

        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => stranger.UpdateAsync(group.GroupId, new UpdateGroupRequest { Name = "Guarded", MonthlyTokenQuota = TestGatewayTiers.PowerCap }, CancellationToken.None));
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => stranger.DeleteAsync(group.GroupId, force: true, CancellationToken.None));
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => stranger.RemoveMemberAsync(group.GroupId, member.UserId, CancellationToken.None));
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => stranger.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None));

        Assert.Empty(_tierSync.Calls);
        Assert.Equal(TestGatewayTiers.StandardCap, (await AllocationAsync(member.UserId)).AllocatedTokens);
    }

    [Fact]
    public async Task RemoveMemberAsync_drops_the_row_reresolves_the_user_and_404s_when_they_are_not_a_member()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var service = CreateService(admin);
        var group = await service.CreateAsync(
            new CreateGroupRequest { Name = "Leavers", MonthlyTokenQuota = TestGatewayTiers.PowerCap },
            CancellationToken.None);
        var member = await SeedUserAsync("Leaver", u => u.ApimSubscriptionId = "sub-leaver");
        _ = await service.AddMemberAsync(group.GroupId, new AddGroupMemberRequest { UserId = member.UserId }, CancellationToken.None);
        _tierSync.Calls.Clear();

        await service.RemoveMemberAsync(group.GroupId, member.UserId, CancellationToken.None);

        Assert.False(await Context.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == group.GroupId && m.UserId == member.UserId));
        var allocation = await AllocationAsync(member.UserId);
        Assert.Equal(TestGatewayTiers.StandardCap, allocation.AllocatedTokens);
        Assert.Equal(QuotaLevelType.SystemDefault, allocation.ResolvedLevelType);
        Assert.Equal([(member.UserId, GatewayTiers.Standard)], _tierSync.Calls);
        _ = await SingleAuditAsync(AuditActions.GroupMemberRemoved);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.RemoveMemberAsync(group.GroupId, member.UserId, CancellationToken.None));
    }

    // -- Helpers --

    /// <summary>Real accessor + real audit + real resolution over this test's context, as DI would wire them per request.</summary>
    private GroupService CreateService(User actor) => CreateServiceForOid(actor.EntraObjectId);

    private GroupService CreateServiceForOid(string oid)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimConstants.Oid, oid)], "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);

        return new GroupService(
            Context,
            new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance),
            TestGatewayTiers.Mapper(),
            accessor,
            new AuditService(Context, new AuditWriter(Context, _clock), accessor),
            _clock,
            NullLogger<GroupService>.Instance);
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

    /// <summary>Pins a user's current-period allocation to a known tier so "the tier changed" is a real transition.</summary>
    private async Task SetAllocationTierAsync(User user, string tierProductId, long? allocatedTokens)
    {
        var allocation = await Context.QuotaAllocations
            .SingleAsync(a => a.UserId == user.UserId && a.PeriodYear == Period.Year && a.PeriodMonth == Period.Month);
        allocation.TierProductId = tierProductId;
        allocation.AllocatedTokens = allocatedTokens;
        _ = await Context.SaveChangesAsync();
    }

    private Task<QuotaAllocation> AllocationAsync(int userId) =>
        Context.QuotaAllocations.AsNoTracking()
            .SingleAsync(a => a.UserId == userId && a.PeriodYear == Period.Year && a.PeriodMonth == Period.Month);

    private async Task<AuditLog> SingleAuditAsync(string action) =>
        Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == action).ToListAsync());
}
