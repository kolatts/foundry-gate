using FoundryGate.Api.Services.Groups;
using FoundryGate.Core.Entra;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Groups.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/groups</c> (spec &#167;4.2; issues #30, #31, #41) — group CRUD, membership, and Entra
/// group sync. Every route is admin-only, so the policy is declared once at class level: a group is a
/// budget policy for other people, and nothing on this surface is a developer's own resource.
/// </summary>
/// <remarks>
/// Thin by contract: the rules — name uniqueness, tier validation, the 409 on deleting a populated
/// group, and the quota re-resolution every membership or policy change triggers — live in
/// <see cref="IGroupService"/> and <see cref="IEntraGroupSyncService"/>, which throw the exception
/// types <c>GlobalExceptionHandler</c> maps to 400/403/404/409.
/// </remarks>
[Authorize(Policy = PolicyNames.AdminOnly)]
public sealed class GroupsController(IGroupService groups, IEntraGroupSyncService entraGroupSync) : ApiControllerBase
{
    /// <summary>Route name for the single-group GET, used to build <c>POST</c>'s <c>Location</c> header.</summary>
    public const string GetGroupRouteName = "GetGroup";

    /// <summary>Groups ordered by name, paged, each with its member count. <c>?search=</c> matches name and description, case-insensitively.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<GroupResponse>>(StatusCodes.Status200OK)]
    public Task<PagedResult<GroupResponse>> ListAsync(
        [FromQuery] GroupQuery filter,
        [FromQuery] PagedRequest paging,
        CancellationToken cancellationToken) =>
        groups.ListAsync(filter, paging, cancellationToken);

    /// <summary>One group with its full member roster.</summary>
    /// <response code="404">No such group.</response>
    [HttpGet("{groupId:int}", Name = GetGroupRouteName)]
    [ProducesResponseType<GroupDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<GroupDetailResponse> GetAsync(int groupId, CancellationToken cancellationToken) =>
        groups.GetAsync(groupId, cancellationToken);

    /// <summary>
    /// Creates a group. <c>201</c> with a <c>Location</c> pointing at <see cref="GetAsync"/>.
    /// </summary>
    /// <response code="400">The monthly token quota is not a configured tier cap (<c>GET /quota/tiers</c> lists them).</response>
    /// <response code="409">A group with that name, or already linked to that <c>entraGroupId</c>, exists.</response>
    [HttpPost]
    [ProducesResponseType<GroupResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<GroupResponse>> CreateAsync(
        [FromBody] CreateGroupRequest request,
        CancellationToken cancellationToken)
    {
        var created = await groups.CreateAsync(request, cancellationToken);

        return CreatedAtRoute(GetGroupRouteName, new { groupId = created.GroupId }, created);
    }

    /// <summary>
    /// Updates name, description and quota policy. A quota change re-resolves every active member's
    /// current-period allocation and moves their gateway tier.
    /// </summary>
    /// <response code="400">The monthly token quota is not a configured tier cap.</response>
    /// <response code="404">No such group.</response>
    /// <response code="409">Another group already has that name.</response>
    [HttpPut("{groupId:int}")]
    [ProducesResponseType<GroupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<GroupResponse> UpdateAsync(
        int groupId,
        [FromBody] UpdateGroupRequest request,
        CancellationToken cancellationToken) =>
        groups.UpdateAsync(groupId, request, cancellationToken);

    /// <summary>
    /// Deletes a group and its memberships (<c>204</c>). Users are never deleted. A group that still
    /// has members needs <c>?force=true</c>.
    /// </summary>
    /// <response code="404">No such group.</response>
    /// <response code="409">The group has members and <c>force</c> was not set.</response>
    [HttpDelete("{groupId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAsync(int groupId, [FromQuery] bool force, CancellationToken cancellationToken)
    {
        await groups.DeleteAsync(groupId, force, cancellationToken);

        return NoContent();
    }

    /// <summary>The group's members, ordered by display name, paged.</summary>
    /// <response code="404">No such group.</response>
    [HttpGet("{groupId:int}/members")]
    [ProducesResponseType<PagedResult<GroupMemberResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<PagedResult<GroupMemberResponse>> ListMembersAsync(
        int groupId,
        [FromQuery] PagedRequest paging,
        CancellationToken cancellationToken) =>
        groups.ListMembersAsync(groupId, paging, cancellationToken);

    /// <summary>
    /// Adds a user to the group and re-resolves their quota. <c>200</c> with the new membership —
    /// there is no per-membership GET for a <c>Location</c> to point at, so this is deliberately not a
    /// <c>201</c>.
    /// </summary>
    /// <response code="404">No such group, or no such user.</response>
    /// <response code="409">The user is already a member, or the group's roster is managed by Entra.</response>
    [HttpPost("{groupId:int}/members")]
    [ProducesResponseType<GroupMemberResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<GroupMemberResponse> AddMemberAsync(
        int groupId,
        [FromBody] AddGroupMemberRequest request,
        CancellationToken cancellationToken) =>
        groups.AddMemberAsync(groupId, request, cancellationToken);

    /// <summary>Removes a membership and re-resolves the user (<c>204</c>).</summary>
    /// <response code="404">No such group, or the user is not a member of it.</response>
    /// <response code="409">The group's roster is managed by Entra; change it in the directory and sync.</response>
    [HttpDelete("{groupId:int}/members/{userId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveMemberAsync(int groupId, int userId, CancellationToken cancellationToken)
    {
        await groups.RemoveMemberAsync(groupId, userId, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Reconciles one group's membership against the Entra group it is linked to (spec &#167;7.3).
    /// Idempotent.
    /// </summary>
    /// <response code="400">The group has no <c>entraGroupId</c>.</response>
    /// <response code="404">No such group.</response>
    /// <response code="503">Entra sync is disabled on this host (<c>Entra:Enabled</c> is false).</response>
    [HttpPost("{groupId:int}/sync-entra")]
    [ProducesResponseType<GroupSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public Task<GroupSyncResult> SyncEntraAsync(int groupId, CancellationToken cancellationToken) =>
        entraGroupSync.SyncAsync(groupId, cancellationToken);

    /// <summary>
    /// Reconciles every Entra-linked group, one summary each. Groups with no <c>entraGroupId</c> are
    /// skipped and do not appear in the result.
    /// </summary>
    /// <response code="503">Entra sync is disabled on this host and at least one group is linked.</response>
    [HttpPost("sync-entra")]
    [ProducesResponseType<IReadOnlyList<GroupSyncResult>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public Task<IReadOnlyList<GroupSyncResult>> SyncAllEntraAsync(CancellationToken cancellationToken) =>
        entraGroupSync.SyncAllAsync(cancellationToken);
}
