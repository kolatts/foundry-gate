using System.Globalization;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Quota;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Groups;

/// <summary>
/// Default <see cref="IEntraGroupSyncService"/>. Scoped — it shares the request's
/// <see cref="AppDbContext"/> so each group's memberships, the allocations they move and the audit row
/// commit atomically. Semantics are documented on the interface.
/// </summary>
/// <remarks>
/// <b>Commit-point discipline</b> (CONVENTIONS.md): re-resolution can reach
/// <see cref="IGatewayTierSync"/> and move members' APIM subscriptions, so the actor is resolved and
/// every refusal is made before it, and the audit row and save run on
/// <see cref="CancellationToken.None"/> once the gateway has actually been touched.
/// </remarks>
public sealed class EntraGroupSyncService(
    AppDbContext dbContext,
    IEntraDirectoryClient directory,
    IQuotaResolutionService quotaResolution,
    ICurrentUserAccessor currentUser,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<EntraGroupSyncService> logger) : IEntraGroupSyncService
{
    /// <inheritdoc />
    public async Task<GroupSyncResult> SyncAsync(int groupId, CancellationToken cancellationToken)
    {
        // Actor first: an unprovisioned admin's 403 must land before the directory read and long
        // before ResolveManyAsync can move anybody's APIM product.
        _ = await currentUser.GetRequiredUserAsync(cancellationToken);

        var group = await dbContext.Groups.SingleOrDefaultAsync(g => g.GroupId == groupId, cancellationToken)
            ?? throw new KeyNotFoundException($"Group {groupId} was not found.");

        return await SyncGroupAsync(group, usersByOid: null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GroupSyncResult>> SyncAllAsync(CancellationToken cancellationToken)
    {
        _ = await currentUser.GetRequiredUserAsync(cancellationToken);

        var groups = await dbContext.Groups
            .Where(group => group.EntraGroupId != string.Empty)
            .OrderBy(group => group.GroupId)
            .ToListAsync(cancellationToken);

        // One snapshot for the run instead of one full Users read per group (#149). Nothing in this
        // path inserts a user, so the map cannot go stale mid-run: the only rows it feeds are
        // memberships, which are keyed on ids it already holds.
        var usersByOid = await LoadUsersByOidAsync(cancellationToken);

        var results = new List<GroupSyncResult>(groups.Count);
        var failed = 0;
        foreach (var group in groups)
        {
            try
            {
                results.Add(await SyncGroupAsync(group, usersByOid, cancellationToken));
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or FeatureNotConfiguredException))
            {
                // Per-group isolation (#149): each group is already its own unit of work, so a Graph
                // fault on group 3 of 5 must not deny the caller the summaries for 4 and 5 — and
                // re-running is idempotent, so nothing is left inconsistent. The run answers 200 with
                // the failure named against the group it belongs to.
                //
                // Two escapes stay whole-run failures on purpose: a cancelled request has no caller
                // left to read a summary, and FeatureNotConfiguredException means Entra is off on this
                // HOST — every group would carry the same 503 message, so the 503 belongs on the
                // response, not repeated in each row.
                failed++;
                results.Add(new GroupSyncResult(group.GroupId, 0, 0, 0, Succeeded: false, Error: exception.Message));
                logger.LogWarning(
                    exception,
                    "Entra group sync could not reconcile group {GroupId} ('{GroupName}'); the run continues with the remaining group(s).",
                    group.GroupId,
                    group.Name);

                // A group whose reconciliation threw part-way may have left pending membership changes
                // tracked but unsaved. They belong to a unit of work that did not commit, so they must
                // not ride along on the next group's SaveChangesAsync.
                DiscardPendingChanges();
            }
        }

        logger.LogInformation(
            "Entra group sync reconciled {GroupCount} linked group(s), {FailedCount} of which failed.",
            results.Count,
            failed);

        return results;
    }

    /// <summary>
    /// Reconciles one group against its linked Entra group, in one unit of work. Shared by both public
    /// entry points; <paramref name="usersByOid"/> is the run's shared oid → user snapshot when this is
    /// one group of a <c>SyncAllAsync</c> pass, and <see langword="null"/> for a single-group sync,
    /// which reads its own (#149).
    /// </summary>
    private async Task<GroupSyncResult> SyncGroupAsync(Group group, IReadOnlyDictionary<string, UserRow>? usersByOid, CancellationToken cancellationToken)
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
        // limit. A SyncAllAsync pass hands its own snapshot down so the table is read once per run
        // rather than once per linked group (#149); a single-group sync reads its own.
        var users = usersByOid ?? await LoadUsersByOidAsync(cancellationToken);

        var desired = new Dictionary<int, UserRow>();
        var skippedUnknown = 0;
        foreach (var objectId in directoryOids)
        {
            if (users.TryGetValue(objectId, out var row))
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
        var activeByUserId = users.Values.ToDictionary(row => row.UserId, row => row.IsActive);
        var reresolved = added
            .Concat(removed.Select(member => member.UserId))
            .Distinct()
            .Where(userId => activeByUserId.GetValueOrDefault(userId))
            .Order()
            .ToList();

        // Resolution overlays the pending membership adds/removes, so this resolves against the roster
        // as it will be once this unit of work commits — and moves the APIM tier for whoever changed.
        // Past this point the gateway may already have accepted a move, so the audit row and the save
        // below run on CancellationToken.None (see the type remarks).
        var resolutions = reresolved.Count == 0
            ? []
            : await quotaResolution.ResolveManyAsync(reresolved, BillingPeriod.Current(timeProvider), cancellationToken);
        var commitToken = resolutions.Any(resolution => resolution.TierSyncRequested)
            ? CancellationToken.None
            : cancellationToken;

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
            commitToken);

        _ = await dbContext.SaveChangesAsync(commitToken);

        logger.LogInformation(
            "Entra group sync for group {GroupId} ('{GroupName}'): {AddedCount} added, {RemovedCount} removed, {SkippedUnknownUserCount} skipped.",
            group.GroupId,
            group.Name,
            added.Count,
            removed.Count,
            skippedUnknown);

        return new GroupSyncResult(group.GroupId, added.Count, removed.Count, skippedUnknown);
    }

    /// <summary>
    /// The whole <c>Users</c> table as oid → <see cref="UserRow"/>. Case-insensitive because oids are
    /// GUID strings whose casing varies by source (token claims vs. Graph vs. hand-seeded rows), and
    /// projected so the read stays three columns wide.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, UserRow>> LoadUsersByOidAsync(CancellationToken cancellationToken) =>
        await dbContext.Users
            .Select(user => new UserRow(user.UserId, user.EntraObjectId, user.IsActive))
            .ToDictionaryAsync(row => row.EntraObjectId, StringComparer.OrdinalIgnoreCase, cancellationToken);

    /// <summary>
    /// Detaches everything the failed group left pending so the next group's <c>SaveChangesAsync</c>
    /// commits only its own work. The shared request <c>AppDbContext</c> makes this the caller's job:
    /// EF has no per-unit-of-work rollback for changes that were never saved.
    /// </summary>
    private void DiscardPendingChanges()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>The three columns of a <see cref="User"/> this reconciliation needs; keeps the whole-table read narrow.</summary>
    private sealed record UserRow(int UserId, string EntraObjectId, bool IsActive);
}
