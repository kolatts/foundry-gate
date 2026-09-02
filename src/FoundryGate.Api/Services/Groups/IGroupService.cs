using FoundryGate.Domain.Common;
using FoundryGate.Domain.Groups.Contracts;

namespace FoundryGate.Api.Services.Groups;

/// <summary>
/// The <c>/api/v1/groups</c> surface (spec &#167;4.2; issues #30 and #31): group CRUD and membership.
/// Groups are how an admin gives a whole team a budget without touching individual user rows, so
/// every write here can move a developer's quota — and, through it, the APIM product their key is
/// scoped to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every quota-visible write re-resolves the affected members.</b> Changing a group's quota,
/// deleting the group, and adding or removing a member all shift levels 3-4 of the resolution chain
/// (spec &#167;3.2) for the users involved, so each of those calls ends with
/// <see cref="Quota.IQuotaResolutionService.ResolveManyAsync"/> over them for the current billing
/// period. That is also what invokes <see cref="Quota.IGatewayTierSync"/> for the members whose tier
/// actually moved — nothing else here touches APIM. Re-resolution covers <b>active</b> members only:
/// a deactivated developer has no key to enforce against and <c>GET /quota/allocations/me</c> refuses
/// to mint them an allocation, so minting one here would contradict it.
/// </para>
/// <para>
/// <b>One unit of work per call.</b> The mutation, its audit row and the allocations it moves are
/// added to the request's <c>AppDbContext</c> and committed by a single <c>SaveChangesAsync</c>
/// (CONVENTIONS.md). Resolution sees the pending mutation because
/// <see cref="Quota.IQuotaResolutionService"/> reads group state through the change tracker.
/// </para>
/// <para>
/// <b>Quota values are tiers (D-013).</b> Create and update run
/// <c>GatewayTierMapper.EnsureValidQuota</c> before persisting, so a group can never carry a budget
/// the gateway has no product for.
/// </para>
/// <para>
/// <b>An Entra-linked group's roster is read-only here.</b> <see cref="AddMemberAsync"/> and
/// <see cref="RemoveMemberAsync"/> refuse with a <c>409</c> when the group has an
/// <c>EntraGroupId</c>: the edit would succeed and then be silently undone by the next
/// <see cref="IEntraGroupSyncService.SyncAsync"/>, which is a worse answer than "no". Change the
/// membership in the directory and sync. Group <em>policy</em> (name, description, quota) stays
/// editable — the directory owns who is in the group, not what the group is worth.
/// </para>
/// </remarks>
public interface IGroupService
{
    /// <summary>
    /// Creates a group. <c>EntraGroupId</c> is optional; supplying it links the group for
    /// <see cref="IEntraGroupSyncService"/> and sets <c>IsEntraSynced</c>. A new group has no members,
    /// so nothing is re-resolved. Audited as <c>group.created</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The quota is not a configured tier cap (→ 400).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">A group with that name, or with that <c>EntraGroupId</c>, already exists (→ 409).</exception>
    Task<GroupResponse> CreateAsync(CreateGroupRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Groups ordered by name, paged, with each group's member count. <c>filter.Search</c> is a
    /// case-insensitive substring over name and description.
    /// </summary>
    Task<PagedResult<GroupResponse>> ListAsync(GroupQuery filter, PagedRequest paging, CancellationToken cancellationToken);

    /// <summary>One group plus its full member roster, ordered by display name.</summary>
    /// <exception cref="KeyNotFoundException">No such group (→ 404).</exception>
    Task<GroupDetailResponse> GetAsync(int groupId, CancellationToken cancellationToken);

    /// <summary>
    /// Updates name, description and quota policy. A change to <c>MonthlyTokenQuota</c> or
    /// <c>IsUnlimited</c> re-resolves every active member. <c>EntraGroupId</c> is deliberately not
    /// updatable — re-pointing a synced group at a different directory group would silently rewrite
    /// its whole roster on the next sync; delete and recreate instead. Whether a guarded
    /// re-link should exist is issue #150. Audited as <c>group.updated</c> with the before/after values.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such group (→ 404).</exception>
    /// <exception cref="ArgumentException">The quota is not a configured tier cap (→ 400).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">Another group already has that name (→ 409).</exception>
    Task<GroupResponse> UpdateAsync(int groupId, UpdateGroupRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a group and its <c>GroupMember</c> rows — never the users themselves, and never their
    /// individual quota overrides. A group that still has members is a <c>409</c> unless
    /// <paramref name="force"/>, so an admin cannot silently drop a whole team's budget with a stray
    /// <c>DELETE</c>. Former members are re-resolved after the memberships are removed, which is what
    /// moves them back down the chain (usually to the system default). Audited as
    /// <c>group.deleted</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such group (→ 404).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">The group has members and <paramref name="force"/> is false (→ 409).</exception>
    Task DeleteAsync(int groupId, bool force, CancellationToken cancellationToken);

    /// <summary>
    /// Adds one user to the group, attributing the membership to the calling admin
    /// (<c>AddedByUserId</c>), and re-resolves them. Audited as <c>group.member-added</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such group, or no such user (→ 404).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">
    /// The user is already a member, or the group's roster is managed by Entra (→ 409) — see
    /// the type remarks on why a linked group refuses manual edits.
    /// </exception>
    Task<GroupMemberResponse> AddMemberAsync(int groupId, AddGroupMemberRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one membership and re-resolves the user. Audited as <c>group.member-removed</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such group, or the user is not a member of it (→ 404).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">The group's roster is managed by Entra (→ 409).</exception>
    Task RemoveMemberAsync(int groupId, int userId, CancellationToken cancellationToken);

    /// <summary>The group's members, ordered by display name, paged.</summary>
    /// <exception cref="KeyNotFoundException">No such group (→ 404).</exception>
    Task<PagedResult<GroupMemberResponse>> ListMembersAsync(int groupId, PagedRequest paging, CancellationToken cancellationToken);
}
