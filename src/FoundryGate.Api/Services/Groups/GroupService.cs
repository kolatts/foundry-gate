using System.Globalization;
using System.Linq.Expressions;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Data;
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
    /// The one read-side projection, so <c>GET /groups</c> and <c>GET /groups/{id}</c> cannot drift.
    /// <c>MemberCount</c> is a correlated <c>COUNT</c> on the navigation rather than a loaded roster.
    /// The entity stores "no description"/"not Entra-linked" as empty strings (non-nullable string
    /// convention); the contract exposes them as null, so the translation happens here.
    /// </summary>
    private static readonly Expression<Func<Group, GroupResponse>> Projection = group => new GroupResponse(
        group.GroupId,
        group.GroupUnique,
        group.Name,
        group.Description == string.Empty ? null : group.Description,
        group.EntraGroupId == string.Empty ? null : group.EntraGroupId,
        group.IsEntraSynced,
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

        var name = request.Name.Trim();
        tierMapper.EnsureValidQuota(request.MonthlyTokenQuota, nameof(request.MonthlyTokenQuota));
        await EnsureNameIsFreeAsync(name, exceptGroupId: null, cancellationToken);

        var entraGroupId = request.EntraGroupId?.Trim() ?? string.Empty;
        var group = new Group
        {
            Name = name,
            Description = request.Description?.Trim() ?? string.Empty,
            EntraGroupId = entraGroupId,
            IsEntraSynced = entraGroupId.Length > 0,
            IsUnlimited = request.IsUnlimited,
            MonthlyTokenQuota = request.MonthlyTokenQuota,
        };

        // The only path here that needs two saves: GroupId is an IDENTITY value, so it does not exist
        // until the insert has run, and the audit row's TargetId must be that id (AuditTargetTypes.Group's
        // contract) rather than a placeholder the admin audit viewer cannot filter on. An explicit
        // transaction keeps the guarantee the single-save pattern exists for — a group can never be
        // created without its audit row. A brand-new group has no members, so nothing is re-resolved.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        _ = dbContext.Groups.Add(group);
        await SaveWithNameConflictAsync(name, exceptGroupId: null, cancellationToken);

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

        await transaction.CommitAsync(cancellationToken);

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
        var resolvedCount = quotaChanged
            ? await ReresolveActiveMembersAsync(groupId, cancellationToken)
            : 0;

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
                MembersReresolvedCount = resolvedCount,
            },
            cancellationToken);

        await SaveWithNameConflictAsync(name, exceptGroupId: groupId, cancellationToken);

        logger.LogInformation(
            "Updated group {GroupId}; quota changed: {QuotaChanged}, members re-resolved: {MembersReresolvedCount}.",
            groupId,
            quotaChanged,
            resolvedCount);

        return await GetGroupResponseAsync(groupId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int groupId, bool force, CancellationToken cancellationToken)
    {
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
        // once the transaction commits.
        _ = await quotaResolution.ResolveManyAsync(affectedUserIds, BillingPeriod.Current(timeProvider), cancellationToken);

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
            cancellationToken);

        _ = await dbContext.SaveChangesAsync(cancellationToken);

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

        _ = await FindTrackedAsync(groupId, cancellationToken);

        var user = await dbContext.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.UserId == request.UserId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {request.UserId} was not found.");

        if (await dbContext.GroupMembers.AnyAsync(member => member.GroupId == groupId && member.UserId == user.UserId, cancellationToken))
        {
            throw new ConflictException($"User {user.UserId} is already a member of group {groupId}.");
        }

        // Attributed to the calling admin. AddedDate is stamped by TimestampInterceptor on save.
        var actor = await currentUser.GetRequiredUserAsync(cancellationToken);
        var membership = new GroupMember
        {
            GroupId = groupId,
            UserId = user.UserId,
            AddedByUserId = actor.UserId,
        };
        _ = dbContext.GroupMembers.Add(membership);

        var reresolved = await ReresolveAsync(user, cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.GroupMemberAdded,
            AuditTargetTypes.Group,
            TargetId(groupId),
            new
            {
                user.UserId,
                user.DisplayName,
                AddedByUserId = actor.UserId,
                Reresolved = reresolved,
            },
            cancellationToken);

        _ = await dbContext.SaveChangesAsync(cancellationToken);

        return new GroupMemberResponse(user.UserId, user.UserUnique, user.DisplayName, user.Email, membership.AddedDate, membership.AddedByUserId);
    }

    /// <inheritdoc />
    public async Task RemoveMemberAsync(int groupId, int userId, CancellationToken cancellationToken)
    {
        _ = await FindTrackedAsync(groupId, cancellationToken);

        var membership = await dbContext.GroupMembers
            .SingleOrDefaultAsync(member => member.GroupId == groupId && member.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {userId} is not a member of group {groupId}.");

        _ = dbContext.GroupMembers.Remove(membership);

        var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == userId, cancellationToken);
        var reresolved = user is not null && await ReresolveAsync(user, cancellationToken);

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
            cancellationToken);

        _ = await dbContext.SaveChangesAsync(cancellationToken);
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

    /// <summary>Case-insensitive on both providers — see the note on the search filter.</summary>
    private async Task EnsureNameIsFreeAsync(string name, int? exceptGroupId, CancellationToken cancellationToken)
    {
        if (await NameIsTakenAsync(name, exceptGroupId, cancellationToken))
        {
            throw new ConflictException(DuplicateNameMessage(name));
        }
    }

    private Task<bool> NameIsTakenAsync(string name, int? exceptGroupId, CancellationToken cancellationToken)
    {
        var comparand = name.ToLowerInvariant();
        var query = dbContext.Groups.AsNoTracking().Where(group => group.Name.ToLower() == comparand);
        if (exceptGroupId is { } excluded)
        {
            query = query.Where(group => group.GroupId != excluded);
        }

        return query.AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Saves, translating the <c>IX_Groups_Name</c> violation two concurrent writers can still produce
    /// after <see cref="EnsureNameIsFreeAsync"/> has passed for both into the same <c>409</c> the
    /// serial case gets. Any other <see cref="DbUpdateException"/> is rethrown untouched — the
    /// conflict is claimed only when the name really is taken in the database.
    /// </summary>
    private async Task SaveWithNameConflictAsync(string name, int? exceptGroupId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            if (!await NameIsTakenAsync(name, exceptGroupId, CancellationToken.None))
            {
                throw;
            }

            logger.LogWarning(exception, "Group name '{GroupName}' was taken concurrently; returning 409.", name);
            throw new ConflictException(DuplicateNameMessage(name), exception);
        }
    }

    private static string DuplicateNameMessage(string name) =>
        $"A group named '{name}' already exists. Group names are unique.";

    private Task<List<int>> ActiveMemberIdsAsync(int groupId, CancellationToken cancellationToken) =>
        MembersOf(groupId)
            .Where(member => member.User.IsActive)
            .OrderBy(member => member.UserId)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);

    private async Task<int> ReresolveActiveMembersAsync(int groupId, CancellationToken cancellationToken)
    {
        var userIds = await ActiveMemberIdsAsync(groupId, cancellationToken);
        _ = await quotaResolution.ResolveManyAsync(userIds, BillingPeriod.Current(timeProvider), cancellationToken);
        return userIds.Count;
    }

    /// <summary>
    /// Re-resolves one member. Deactivated users are skipped for the reason on
    /// <see cref="IGroupService"/>: they hold no enforceable key, and <c>/quota/allocations/me</c>
    /// refuses to mint them an allocation.
    /// </summary>
    private async Task<bool> ReresolveAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.IsActive)
        {
            return false;
        }

        _ = await quotaResolution.ResolveManyAsync([user.UserId], BillingPeriod.Current(timeProvider), cancellationToken);
        return true;
    }
}
