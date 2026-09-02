using System.Globalization;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Quota;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Groups;

/// <summary>
/// Default <see cref="IEntraGroupSyncService"/>. Scoped — it shares the request's
/// <see cref="AppDbContext"/> so each group's memberships, the allocations they move and the audit row
/// commit atomically. Semantics are documented on the interface.
/// </summary>
public sealed class EntraGroupSyncService(
    AppDbContext dbContext,
    IEntraDirectoryClient directory,
    IQuotaResolutionService quotaResolution,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<EntraGroupSyncService> logger) : IEntraGroupSyncService
{
    /// <inheritdoc />
    public async Task<GroupSyncResult> SyncAsync(int groupId, CancellationToken cancellationToken)
    {
        var group = await dbContext.Groups.SingleOrDefaultAsync(g => g.GroupId == groupId, cancellationToken)
            ?? throw new KeyNotFoundException($"Group {groupId} was not found.");

        return await SyncGroupAsync(group, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GroupSyncResult>> SyncAllAsync(CancellationToken cancellationToken)
    {
        var groups = await dbContext.Groups
            .Where(group => group.EntraGroupId != string.Empty)
            .OrderBy(group => group.GroupId)
            .ToListAsync(cancellationToken);

        var results = new List<GroupSyncResult>(groups.Count);
        foreach (var group in groups)
        {
            results.Add(await SyncGroupAsync(group, cancellationToken));
        }

        logger.LogInformation("Entra group sync reconciled {GroupCount} linked group(s).", results.Count);

        return results;
    }

    private async Task<GroupSyncResult> SyncGroupAsync(Group group, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(group.EntraGroupId))
        {
            // No paramName: this message is the ProblemDetails detail an admin reads on the 400, and
            // "(Parameter 'group')" is noise there.
            throw new ArgumentException(
                $"Group {group.GroupId} ('{group.Name}') is not linked to an Entra group, so there is nothing to sync it against. Recreate it with an entraGroupId to enable sync.");
        }

        // Streamed from Graph (transitive, so nested groups flatten to their people) into a set: the
        // directory may list a principal twice across pages and a duplicate must not become a second
        // membership.
        var directoryOids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var objectId in directory.ListGroupMemberIdsAsync(group.EntraGroupId, transitive: true, cancellationToken))
        {
            _ = directoryOids.Add(objectId);
        }

        // The whole user table by oid, like EntraUserSyncService: a group can hold thousands of members
        // and a `WHERE EntraObjectId IN (...)` over that set would blow past the provider's parameter
        // limit. Case-insensitive because oids are GUID strings whose casing varies by source.
        var usersByOid = await dbContext.Users
            .Select(user => new UserRow(user.UserId, user.EntraObjectId, user.IsActive))
            .ToDictionaryAsync(row => row.EntraObjectId, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var desired = new Dictionary<int, UserRow>();
        var skippedUnknown = 0;
        foreach (var objectId in directoryOids)
        {
            if (usersByOid.TryGetValue(objectId, out var row))
            {
                desired[row.UserId] = row;
            }
            else
            {
                skippedUnknown++;
            }
        }

        var current = await dbContext.GroupMembers
            .Where(member => member.GroupId == group.GroupId)
            .ToListAsync(cancellationToken);
        var currentUserIds = current.Select(member => member.UserId).ToHashSet();

        var added = desired.Keys.Where(userId => !currentUserIds.Contains(userId)).Order().ToList();
        foreach (var userId in added)
        {
            // AddedByUserId stays null: the directory chose this membership, not the calling admin.
            _ = dbContext.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = userId });
        }

        var removed = current.Where(member => !desired.ContainsKey(member.UserId)).ToList();
        dbContext.GroupMembers.RemoveRange(removed);

        // Only the users whose membership actually moved, and only the active ones — see IGroupService.
        // IsActive comes from the map already in hand; no second trip for it.
        var activeByUserId = usersByOid.Values.ToDictionary(row => row.UserId, row => row.IsActive);
        var reresolved = added
            .Concat(removed.Select(member => member.UserId))
            .Distinct()
            .Where(userId => activeByUserId.GetValueOrDefault(userId))
            .Order()
            .ToList();

        // Resolution overlays the pending membership adds/removes, so this resolves against the roster
        // as it will be once this unit of work commits — and moves the APIM tier for whoever changed.
        _ = await quotaResolution.ResolveManyAsync(reresolved, BillingPeriod.Current(timeProvider), cancellationToken);

        if (skippedUnknown > 0)
        {
            logger.LogWarning(
                "Entra group sync for group {GroupId} ('{GroupName}') skipped {SkippedUnknownUserCount} directory member(s) with no FoundryGate user row. Run POST /users/sync to import them, then sync again.",
                group.GroupId,
                group.Name,
                skippedUnknown);
        }

        // One row per group per run (not one per membership): the counts are the story, and a 500-member
        // group would otherwise bury every other action in the audit viewer.
        _ = await audit.LogAsync(
            AuditActions.GroupEntraSynced,
            AuditTargetTypes.Group,
            group.GroupId.ToString(CultureInfo.InvariantCulture),
            new
            {
                group.EntraGroupId,
                DirectoryMemberCount = directoryOids.Count,
                AddedCount = added.Count,
                RemovedCount = removed.Count,
                SkippedUnknownUserCount = skippedUnknown,
                AddedUserIds = added.ToArray(),
                RemovedUserIds = removed.Select(member => member.UserId).ToArray(),
                ReresolvedUserIds = reresolved.ToArray(),
            },
            cancellationToken);

        _ = await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Entra group sync for group {GroupId} ('{GroupName}'): {AddedCount} added, {RemovedCount} removed, {SkippedUnknownUserCount} skipped.",
            group.GroupId,
            group.Name,
            added.Count,
            removed.Count,
            skippedUnknown);

        return new GroupSyncResult(group.GroupId, added.Count, removed.Count, skippedUnknown);
    }

    /// <summary>The three columns of a <see cref="User"/> this reconciliation needs; keeps the whole-table read narrow.</summary>
    private sealed record UserRow(int UserId, string EntraObjectId, bool IsActive);
}
