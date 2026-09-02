using System.Globalization;
using System.Linq.Expressions;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Quota;
using FoundryGate.Data;
using FoundryGate.Data.Concurrency;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Extensions;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Quota;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Groups;

/// <summary>
/// Default <see cref="IGroupService"/>. Scoped: shares the request's <see cref="AppDbContext"/> with
/// <see cref="IQuotaResolutionService"/> and <see cref="IAuditService"/>, so a group mutation, the
/// allocations it moves and its audit row are one unit of work. Semantics are on the interface.
/// </summary>
/// <remarks>
/// <b>Commit-point discipline</b> (CONVENTIONS.md "external side effects have a commit point"):
/// re-resolution can reach <see cref="IGatewayTierSync"/> and move a developer's APIM subscription
/// between tier products. Every method therefore resolves the actor and performs every refusal
/// <em>before</em> calling <see cref="IQuotaResolutionService.ResolveManyAsync"/>, and — when that call
/// actually moved the gateway (<see cref="QuotaResolution.TierSyncRequested"/>) — writes its audit row
/// and saves on <see cref="CancellationToken.None"/>, so a client that hangs up mid-request cannot
/// leave APIM moved with nothing in the database to show for it. The same rule applies to every other
/// caller of quota resolution; issue #163 sweeps the ones outside this area.
/// </remarks>
public sealed class GroupService(
    AppDbContext dbContext,
    IQuotaResolutionService quotaResolution,
    GatewayTierMapper tierMapper,
    ICurrentUserAccessor currentUser,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<GroupService> logger) : IGroupService
{
    /// <summary>
    /// Identifiers a unique violation on the group-name index carries, per provider: SQL Server names
    /// the index ("Cannot insert duplicate key row … with unique index 'IX_Groups_Name'"), SQLite names
    /// the column ("UNIQUE constraint failed: Groups.Name"). Matching these rather than re-querying is
    /// what makes the 409 collation-agnostic — on an accent-insensitive database the index rejects
    /// "Résumé" against "Resume" and a <c>LOWER(Name)</c> re-query would not have found the collision,
    /// turning a conflict into a 500. Both markers are identifiers, so neither is affected by a
    /// localized server message.
    /// </summary>
    private static readonly string[] NameIndexMarkers = ["IX_Groups_Name", "Groups.Name"];

    /// <summary>Same idea for the Entra-link index; see <see cref="NameIndexMarkers"/>.</summary>
    private static readonly string[] EntraGroupIdIndexMarkers = ["IX_Groups_EntraGroupId", "Groups.EntraGroupId"];

    /// <summary>
    /// The one read-side projection, so <c>GET /groups</c> and <c>GET /groups/{id}</c> cannot drift.
    /// <c>MemberCount</c> is a correlated <c>COUNT</c> on the navigation rather than a loaded roster.
    /// The entity stores "no description"/"not Entra-linked" as empty strings (non-nullable string
    /// convention); the contract exposes them as null, so the translation happens here — as does
    /// <c>IsEntraSynced</c>, which is <em>derived</em> from the link rather than stored, so the two can
    /// never disagree.
    /// </summary>
    private static readonly Expression<Func<Group, GroupResponse>> Projection = group => new GroupResponse(
        group.GroupId,
        group.GroupUnique,
        group.Name,
        group.Description == string.Empty ? null : group.Description,
        group.EntraGroupId == string.Empty ? null : group.EntraGroupId,
        group.EntraGroupId != string.Empty,
        group.IsUnlimited,
        group.MonthlyTokenQuota,
        group.GroupMemberships.Count,
        group.CreatedDate);

    private static readonly Expression<Func<GroupMember, GroupMemberResponse>> MemberProjection = member => new GroupMemberResponse(
        member.UserId,
        member.User.UserUnique,
        member.User.DisplayName,
        member.User.Email,
        member.AddedDate,
        member.AddedByUserId);

    /// <inheritdoc />
    public async Task<GroupResponse> CreateAsync(CreateGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await EnsureActorAsync(cancellationToken);

        var name = request.Name.Trim();
        var entraGroupId = request.EntraGroupId?.Trim() ?? string.Empty;
        tierMapper.EnsureValidQuota(request.MonthlyTokenQuota, nameof(request.MonthlyTokenQuota));
        await EnsureNameIsFreeAsync(name, exceptGroupId: null, cancellationToken);
        await EnsureEntraLinkIsFreeAsync(entraGroupId, exceptGroupId: null, cancellationToken);

        var group = new Group
        {
            Name = name,
            Description = request.Description?.Trim() ?? string.Empty,
            EntraGroupId = entraGroupId,
            IsUnlimited = request.IsUnlimited,
            MonthlyTokenQuota = request.MonthlyTokenQuota,
        };

        // The only path here that needs two saves: GroupId is an IDENTITY value, so it does not exist
        // until the insert has run, and the audit row's TargetId must be that id (AuditTargetTypes.Group's
        // contract) rather than a placeholder the admin audit viewer cannot filter on. A transaction keeps
        // the guarantee the single-save pattern exists for — a group can never be created without its
        // audit row. Joins an ambient transaction rather than opening a nested one (which EF refuses) when
        // an orchestrating service already owns the unit of work; the ApimKeyService precedent. A
        // brand-new group has no members, so nothing is re-resolved and no gateway call is in play.
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        _ = dbContext.Groups.Add(group);
        await SaveGroupAsync(name, entraGroupId, exceptGroupId: null, cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.GroupCreated,
            AuditTargetTypes.Group,
            TargetId(group.GroupId),
            new
            {
                group.Name,
                group.Description,
                group.EntraGroupId,
                group.IsUnlimited,
                group.MonthlyTokenQuota,
            },
            cancellationToken);
        _ = await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("Created group {GroupId} ({GroupName}).", group.GroupId, group.Name);

        return await GetGroupResponseAsync(group.GroupId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PagedResult<GroupResponse>> ListAsync(GroupQuery filter, PagedRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(paging);

        IQueryable<Group> query = dbContext.Groups.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // ToLower() on both sides rather than a bare Contains: SQL Server's default collation is
            // case-insensitive and SQLite's is not, and a search box that behaves differently in the
            // test harness than in production is worse than one that cannot use the index. Group
            // counts are in the dozens.
            var search = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(group =>
                group.Name.ToLower().Contains(search) || group.Description.ToLower().Contains(search));
        }

        return query
            .OrderBy(group => group.Name)
            .ThenBy(group => group.GroupId)
            .Select(Projection)
            .ToPagedAsync(paging, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GroupDetailResponse> GetAsync(int groupId, CancellationToken cancellationToken)
    {
        var group = await GetGroupResponseAsync(groupId, cancellationToken);

        var members = await MembersOf(groupId)
            .OrderBy(member => member.User.DisplayName)
            .ThenBy(member => member.UserId)
            .Select(MemberProjection)
            .ToListAsync(cancellationToken);

        return new GroupDetailResponse(group, members);
    }

    /// <inheritdoc />
    public async Task<GroupResponse> UpdateAsync(int groupId, UpdateGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Actor and every refusal first — ReresolveAsync below can move APIM.
        await EnsureActorAsync(cancellationToken);
        var group = await FindTrackedAsync(groupId, cancellationToken);
        var name = request.Name.Trim();
        tierMapper.EnsureValidQuota(request.MonthlyTokenQuota, nameof(request.MonthlyTokenQuota));
        await EnsureNameIsFreeAsync(name, exceptGroupId: groupId, cancellationToken);

        var before = new
        {
            group.Name,
            group.Description,
            group.IsUnlimited,
            group.MonthlyTokenQuota,
        };
        var quotaChanged = group.IsUnlimited != request.IsUnlimited || group.MonthlyTokenQuota != request.MonthlyTokenQuota;

        group.Name = name;
        group.Description = request.Description?.Trim() ?? string.Empty;
        group.IsUnlimited = request.IsUnlimited;
        group.MonthlyTokenQuota = request.MonthlyTokenQuota;

        // Levels 3-4 of the chain just moved for everyone in the group; resolution reads the edited
        // (still unsaved) Group through the change tracker, so this sees the new policy and the whole
        // thing commits together.
        var memberIds = quotaChanged ? await ActiveMemberIdsAsync(groupId, cancellationToken) : [];
        var gatewayMoved = await ReresolveAsync(memberIds, cancellationToken);
        var commitToken = CommitToken.For(gatewayMoved, cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.GroupUpdated,
            AuditTargetTypes.Group,
            TargetId(groupId),
            new
            {
                Before = before,
                After = new
                {
                    group.Name,
                    group.Description,
                    group.IsUnlimited,
                    group.MonthlyTokenQuota,
                },
                QuotaChanged = quotaChanged,
                MembersReresolvedCount = memberIds.Count,
            },
            commitToken);

        await SaveGroupAsync(name, group.EntraGroupId, exceptGroupId: groupId, commitToken);

        logger.LogInformation(
            "Updated group {GroupId}; quota changed: {QuotaChanged}, members re-resolved: {MembersReresolvedCount}.",
            groupId,
            quotaChanged,
            memberIds.Count);

        return await GetGroupResponseAsync(groupId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int groupId, bool force, CancellationToken cancellationToken)
    {
        await EnsureActorAsync(cancellationToken);
        var group = await FindTrackedAsync(groupId, cancellationToken);

        var memberships = await dbContext.GroupMembers
            .Where(member => member.GroupId == groupId)
            .ToListAsync(cancellationToken);

        if (memberships.Count > 0 && !force)
        {
            throw new ConflictException(
                $"Group {groupId} still has {memberships.Count} member(s). Remove them first, or repeat the request with ?force=true to delete the group and its memberships (the users themselves are never deleted).");
        }

        var affectedUserIds = await ActiveMemberIdsAsync(groupId, cancellationToken);

        dbContext.GroupMembers.RemoveRange(memberships);
        _ = dbContext.Groups.Remove(group);

        // After the removals, not before: resolution overlays the pending deletes, so the former
        // members resolve down the chain (usually to the system default) exactly as they will read
        // once this commits.
        var gatewayMoved = await ReresolveAsync(affectedUserIds, cancellationToken);
        var commitToken = CommitToken.For(gatewayMoved, cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.GroupDeleted,
            AuditTargetTypes.Group,
            TargetId(groupId),
            new
            {
                group.Name,
                group.EntraGroupId,
                group.IsUnlimited,
                group.MonthlyTokenQuota,
                Forced = force,
                RemovedMemberCount = memberships.Count,
                MembersReresolvedCount = affectedUserIds.Count,
            },
            commitToken);

        _ = await dbContext.SaveChangesAsync(commitToken);

        logger.LogInformation(
            "Deleted group {GroupId} ({GroupName}) with {RemovedMemberCount} membership(s).",
            groupId,
            group.Name,
            memberships.Count);
    }

    /// <inheritdoc />
    public async Task<GroupMemberResponse> AddMemberAsync(int groupId, AddGroupMemberRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = await currentUser.GetRequiredUserAsync(cancellationToken);
        var group = await FindTrackedAsync(groupId, cancellationToken);
        EnsureRosterIsEditable(group);

        var user = await dbContext.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.UserId == request.UserId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {request.UserId} was not found.");

        if (await dbContext.GroupMembers.AnyAsync(member => member.GroupId == groupId && member.UserId == user.UserId, cancellationToken))
        {
            throw new ConflictException($"User {user.UserId} is already a member of group {groupId}.");
        }

        // Attributed to the calling admin. AddedDate is stamped by TimestampInterceptor on save.
        var membership = new GroupMember
        {
            GroupId = groupId,
            UserId = user.UserId,
            AddedByUserId = actor.UserId,
        };
        _ = dbContext.GroupMembers.Add(membership);

        var gatewayMoved = await ReresolveAsync(user.IsActive ? [user.UserId] : [], cancellationToken);
        var commitToken = CommitToken.For(gatewayMoved, cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.GroupMemberAdded,
            AuditTargetTypes.Group,
            TargetId(groupId),
            new
            {
                user.UserId,
                user.DisplayName,
                AddedByUserId = actor.UserId,
                Reresolved = user.IsActive,
            },
            commitToken);

        _ = await dbContext.SaveChangesAsync(commitToken);

        return new GroupMemberResponse(user.UserId, user.UserUnique, user.DisplayName, user.Email, membership.AddedDate, membership.AddedByUserId);
    }

    /// <inheritdoc />
    public async Task RemoveMemberAsync(int groupId, int userId, CancellationToken cancellationToken)
    {
        await EnsureActorAsync(cancellationToken);
        var group = await FindTrackedAsync(groupId, cancellationToken);
        EnsureRosterIsEditable(group);

        var membership = await dbContext.GroupMembers
            .SingleOrDefaultAsync(member => member.GroupId == groupId && member.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {userId} is not a member of group {groupId}.");

        var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == userId, cancellationToken);

        _ = dbContext.GroupMembers.Remove(membership);

        var reresolved = user is { IsActive: true };
        var gatewayMoved = await ReresolveAsync(reresolved ? [userId] : [], cancellationToken);
        var commitToken = CommitToken.For(gatewayMoved, cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.GroupMemberRemoved,
            AuditTargetTypes.Group,
            TargetId(groupId),
            new
            {
                UserId = userId,
                membership.AddedByUserId,
                membership.AddedDate,
                Reresolved = reresolved,
            },
            commitToken);

        _ = await dbContext.SaveChangesAsync(commitToken);
    }

    /// <inheritdoc />
    public async Task<PagedResult<GroupMemberResponse>> ListMembersAsync(int groupId, PagedRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        await EnsureGroupExistsAsync(groupId, cancellationToken);

        return await MembersOf(groupId)
            .OrderBy(member => member.User.DisplayName)
            .ThenBy(member => member.UserId)
            .Select(MemberProjection)
            .ToPagedAsync(paging, cancellationToken);
    }

    // -- Helpers --

    private static string TargetId(int groupId) => groupId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves the caller up front so an unprovisioned admin's 403 lands before anything external
    /// happens, rather than out of <see cref="IAuditService.LogAsync"/> after a tier move.
    /// </summary>
    private async Task EnsureActorAsync(CancellationToken cancellationToken) =>
        _ = await currentUser.GetRequiredUserAsync(cancellationToken);

    /// <summary>
    /// An Entra-linked group's roster belongs to the directory: a manual add or remove here would be
    /// silently undone by the next <c>sync-entra</c>, which is worse than refusing it.
    /// </summary>
    private static void EnsureRosterIsEditable(Group group)
    {
        if (group.EntraGroupId.Length > 0)
        {
            throw new ConflictException(
                $"Group {group.GroupId} ('{group.Name}') has its roster managed by Entra group {group.EntraGroupId}; memberships cannot be edited directly because the next sync would undo the change. " +
                $"Change the membership in the Entra group and run POST /groups/{group.GroupId}/sync-entra.");
        }
    }

    /// <summary>
    /// Re-resolves <paramref name="userIds"/> for the current period and reports whether any of them
    /// actually moved at the gateway — which is what decides the commit token (see the type remarks).
    /// An empty list is a no-op, so callers pass one rather than branching.
    /// </summary>
    private async Task<bool> ReresolveAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return false;
        }

        var resolutions = await quotaResolution.ResolveManyAsync(userIds, BillingPeriod.Current(timeProvider), GatewayTierSyncMode.Immediate, cancellationToken);

        return resolutions.Any(resolution => resolution.TierSyncRequested);
    }

    private IQueryable<GroupMember> MembersOf(int groupId) =>
        dbContext.GroupMembers.AsNoTracking().Where(member => member.GroupId == groupId);

    private async Task<Group> FindTrackedAsync(int groupId, CancellationToken cancellationToken) =>
        await dbContext.Groups.SingleOrDefaultAsync(group => group.GroupId == groupId, cancellationToken)
        ?? throw new KeyNotFoundException($"Group {groupId} was not found.");

    private async Task EnsureGroupExistsAsync(int groupId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Groups.AnyAsync(group => group.GroupId == groupId, cancellationToken))
        {
            throw new KeyNotFoundException($"Group {groupId} was not found.");
        }
    }

    private async Task<GroupResponse> GetGroupResponseAsync(int groupId, CancellationToken cancellationToken) =>
        await dbContext.Groups.AsNoTracking()
            .Where(group => group.GroupId == groupId)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new KeyNotFoundException($"Group {groupId} was not found.");

    /// <summary>
    /// The friendly pre-check for the name, case-insensitively on both providers (see the note on the
    /// search filter). <see cref="SaveGroupAsync"/> is what makes uniqueness true under concurrency.
    /// </summary>
    private async Task EnsureNameIsFreeAsync(string name, int? exceptGroupId, CancellationToken cancellationToken)
    {
        var comparand = name.ToLowerInvariant();
        var query = dbContext.Groups.AsNoTracking().Where(group => group.Name.ToLower() == comparand);
        if (exceptGroupId is { } excluded)
        {
            query = query.Where(group => group.GroupId != excluded);
        }

        if (await query.AnyAsync(cancellationToken))
        {
            throw new ConflictException(DuplicateNameMessage(name));
        }
    }

    /// <summary>
    /// Two groups linked to the same Entra group would both claim its members and hand them the max of
    /// both quotas off one directory group — deterministic, but never what an admin who double-pasted a
    /// GUID meant. Backed by the filtered unique index <c>IX_Groups_EntraGroupId</c>; an empty link is
    /// unconstrained, which is why the index is filtered.
    /// </summary>
    private async Task EnsureEntraLinkIsFreeAsync(string entraGroupId, int? exceptGroupId, CancellationToken cancellationToken)
    {
        if (entraGroupId.Length == 0)
        {
            return;
        }

        var query = dbContext.Groups.AsNoTracking().Where(group => group.EntraGroupId == entraGroupId);
        if (exceptGroupId is { } excluded)
        {
            query = query.Where(group => group.GroupId != excluded);
        }

        if (await query.AnyAsync(cancellationToken))
        {
            throw new ConflictException(DuplicateEntraLinkMessage(entraGroupId));
        }
    }

    /// <summary>
    /// Saves, translating the unique-index violations two concurrent writers can still produce after the
    /// pre-checks have passed for both into the same <c>409</c> the serial case gets. Detection is by
    /// the identifier the provider names in its error (see <see cref="NameIndexMarkers"/>) rather than by
    /// re-querying, so it agrees with whatever collation the index actually used. Any other
    /// <see cref="DbUpdateException"/> is rethrown untouched.
    /// </summary>
    private async Task SaveGroupAsync(string name, string entraGroupId, int? exceptGroupId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueIndexViolation.Mentions(exception, NameIndexMarkers))
        {
            logger.LogWarning(exception, "Group name '{GroupName}' was taken concurrently; returning 409.", name);
            throw new ConflictException(DuplicateNameMessage(name), exception);
        }
        catch (DbUpdateException exception) when (UniqueIndexViolation.Mentions(exception, EntraGroupIdIndexMarkers))
        {
            logger.LogWarning(exception, "Entra group {EntraGroupId} was linked concurrently (group {ExceptGroupId} excluded); returning 409.", entraGroupId, exceptGroupId);
            throw new ConflictException(DuplicateEntraLinkMessage(entraGroupId), exception);
        }
    }

    private static string DuplicateNameMessage(string name) =>
        $"A group named '{name}' already exists. Group names are unique.";

    private static string DuplicateEntraLinkMessage(string entraGroupId) =>
        $"Another group is already linked to Entra group {entraGroupId}. One Entra group can back at most one FoundryGate group.";

    private Task<List<int>> ActiveMemberIdsAsync(int groupId, CancellationToken cancellationToken) =>
        MembersOf(groupId)
            .Where(member => member.User.IsActive)
            .OrderBy(member => member.UserId)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);
}
