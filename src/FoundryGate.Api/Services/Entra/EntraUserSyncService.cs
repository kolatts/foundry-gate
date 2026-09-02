using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Users.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Entra;

/// <summary>
/// Default <see cref="IEntraUserSyncService"/>. Loads the whole <c>Users</c> table once (tracked —
/// it is mutated in place), asks <see cref="IEntraDirectoryClient"/> for the assigned users, and
/// commits every insert/update/deprovision together with its audit row. Semantics are documented on
/// the interface.
/// </summary>
/// <remarks>
/// The run is wrapped in one database transaction so a departure — which now runs the full
/// deprovision pipeline (<see cref="IUserLifecycleService"/>, plan 21 Trigger B: delete the APIM
/// subscription, hard-stop the allocation, reject pending requests) and saves as it goes — commits
/// with the adds and updates rather than ahead of them. The orchestrator and the key service both join
/// an already-open transaction instead of starting their own, so this is the only <c>BeginTransaction</c>
/// in the path.
/// </remarks>
public sealed class EntraUserSyncService(
    AppDbContext dbContext,
    IEntraDirectoryClient directory,
    IUserLifecycleService lifecycle,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<EntraUserSyncService> logger) : IEntraUserSyncService
{
    /// <summary>
    /// <c>User.DisplayName</c> is <c>[StringLength(200)]</c>; Entra allows 256. One over-long name
    /// must not turn the whole run into a SQL truncation error, so it is clipped here (the only
    /// synced field whose directory maximum exceeds its column: mail ≤ 256 &lt; 320, employeeId ≤ 16
    /// &lt; 64).
    /// </summary>
    private const int DisplayNameMaxLength = 200;

    /// <inheritdoc />
    public async Task<UserSyncResult> SyncUsersAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // One transaction for the whole run: the deprovision pipeline below saves per departed user, and
        // those saves must not commit ahead of (or without) the adds, updates and the users.synced row.
        // IUserLifecycleService and IApimKeyService both join an open transaction rather than nesting.
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // Tracked on purpose: matched rows are updated in place and saved below. EntraObjectId is
        // unique, so the dictionary cannot collide. Case-insensitive because oids are GUID strings
        // whose casing varies by source (token claims vs. Graph vs. hand-seeded rows).
        var existingByOid = await dbContext.Users
            .ToDictionaryAsync(u => u.EntraObjectId, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var assigned = await directory.ListAssignedUsersAsync(cancellationToken);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedOids = new List<string>();
        var updated = 0;

        foreach (var entraUser in assigned.Users)
        {
            if (!seen.Add(entraUser.ObjectId))
            {
                continue; // a principal listed twice must count once
            }

            if (existingByOid.TryGetValue(entraUser.ObjectId, out var user))
            {
                ApplyDirectoryFields(user, entraUser, now);
                updated++;
            }
            else
            {
                var newUser = new User { EntraObjectId = entraUser.ObjectId };
                ApplyDirectoryFields(newUser, entraUser, now);
                dbContext.Users.Add(newUser);
                addedOids.Add(entraUser.ObjectId);
            }
        }

        var activeAbsent = existingByOid.Values
            .Where(u => u.IsActive && !seen.Contains(u.EntraObjectId))
            .OrderBy(u => u.UserId)
            .ToList();

        var skippedGroups = assigned.SkippedGroupAssignments;
        List<User> deactivated;
        if (skippedGroups.Count > 0)
        {
            // Users assigned through a group are invisible until #121 expands group assignees, so an
            // active user missing from the user list may simply be covered by one of these groups.
            // Departure detection is suspended for the run rather than flipping everyone to inactive
            // (and, once #65 lands, deleting every APIM subscription) on a 200 with a clean audit row.
            deactivated = [];
            logger.LogWarning(
                "Entra user sync skipped departure detection: {GroupCount} app-role assignment(s) are granted to groups, which are not expanded yet (#121). {ActiveAbsentCount} active user(s) absent from the assigned-user list were left active. Groups: {Groups}",
                skippedGroups.Count,
                activeAbsent.Count,
                string.Join("; ", skippedGroups.Select(g => $"{g.DisplayName} ({g.GroupObjectId})")));
        }
        else if (seen.Count == 0 && activeAbsent.Count > 0)
        {
            // An empty assigned-user list with a populated table is almost always a wrong service
            // principal or a missing Graph role, not a mass departure. Deactivating everyone on that
            // signal is not acceptable.
            throw new ConflictException(
                $"Entra returned no assigned users while {activeAbsent.Count} active user(s) exist locally; refusing to deactivate " +
                "every user. Check Entra:ServicePrincipalObjectId / AzureAd:ClientId and that developers are assigned to the application.");
        }
        else
        {
            deactivated = activeAbsent;
            foreach (var user in deactivated)
            {
                user.LastSyncedDate = now;

                // Plan 21 deprovision Trigger B, in full (#65): the APIM subscription is deleted, the
                // current allocation hard-stopped and pending increase requests rejected — not just a
                // flag flip that would leave a departed employee holding a working gateway key. No HTTP
                // caller is involved, so its audit rows are system-attributed.
                await lifecycle.DeprovisionAsync(DeprovisionTrigger.EntraDeparture, user.UserId, cancellationToken);
            }
        }

        var result = new UserSyncResult(addedOids.Count, updated, deactivated.Count, skippedGroups.Count);

        // Audit before save, same context → commits atomically with the rows above (CONVENTIONS.md).
        // No single target: the whole table is the subject. Deactivated ids are known (saved rows);
        // added rows have no ids until save, so they are recorded by oid.
        _ = await audit.LogAsync(
            AuditActions.UsersSynced,
            string.Empty,
            string.Empty,
            new
            {
                result.AddedCount,
                result.UpdatedCount,
                result.DeactivatedCount,
                result.SkippedGroupAssignmentCount,
                DeactivatedUserIds = deactivated.Select(u => u.UserId).ToArray(),
                AddedEntraObjectIds = addedOids.ToArray(),
                DepartureDetectionSuspended = skippedGroups.Count > 0,
                SkippedGroupAssignments = skippedGroups.Select(g => new { g.GroupObjectId, g.DisplayName }).ToArray(),
            },
            cancellationToken);

        _ = await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Entra user sync complete: {Added} added, {Updated} updated, {Deactivated} deactivated, {SkippedGroups} group assignment(s) skipped.",
            result.AddedCount,
            result.UpdatedCount,
            result.DeactivatedCount,
            result.SkippedGroupAssignmentCount);

        return result;
    }

    private static void ApplyDirectoryFields(User user, EntraUser entraUser, DateTimeOffset now)
    {
        user.DisplayName = entraUser.DisplayName.Length > DisplayNameMaxLength
            ? entraUser.DisplayName[..DisplayNameMaxLength]
            : entraUser.DisplayName;
        user.Email = entraUser.Email;
        user.EmployeeId = entraUser.EmployeeId;
        user.LastSyncedDate = now;
    }
}
