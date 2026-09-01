using FoundryGate.Api.Services.Audit;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Users.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Entra;

/// <summary>
/// Default <see cref="IEntraUserSyncService"/>. Loads the whole <c>Users</c> table once (tracked —
/// it is mutated in place), streams the directory through <see cref="IEntraDirectoryClient"/>, and
/// commits every insert/update/deactivation together with its audit row in a single
/// <c>SaveChangesAsync</c>. Semantics are documented on the interface.
/// </summary>
public sealed class EntraUserSyncService(
    AppDbContext dbContext,
    IEntraDirectoryClient directory,
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

        // Tracked on purpose: matched rows are updated in place and saved below. EntraObjectId is
        // unique, so the dictionary cannot collide. Case-insensitive because oids are GUID strings
        // whose casing varies by source (token claims vs. Graph vs. hand-seeded rows).
        var existingByOid = await dbContext.Users
            .ToDictionaryAsync(u => u.EntraObjectId, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var updated = 0;

        await foreach (var entraUser in directory.ListAssignedUsersAsync(cancellationToken))
        {
            if (!seen.Add(entraUser.ObjectId))
            {
                continue; // a principal with several role assignments must count once
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
                added++;
            }
        }

        var activeAbsent = existingByOid.Values
            .Where(u => u.IsActive && !seen.Contains(u.EntraObjectId))
            .OrderBy(u => u.UserId)
            .ToList();

        if (seen.Count == 0 && activeAbsent.Count > 0)
        {
            // An empty assigned-user list with a populated table is almost always a wrong service
            // principal or a missing Graph role, not a mass departure. Deactivating everyone (and,
            // once #65 lands, deleting every APIM subscription) on that signal is not acceptable.
            throw new ConflictException(
                $"Entra returned no assigned users while {activeAbsent.Count} active user(s) exist locally; refusing to deactivate " +
                "every user. Check Entra:ServicePrincipalObjectId / AzureAd:ClientId and that developers are assigned to the application.");
        }

        foreach (var user in activeAbsent)
        {
            // Flag only — the full Entra-departure deprovision (APIM subscription deletion, hard stop,
            // pending-request cancellation; plan #21 trigger B) is issue #65's IUserLifecycleService.
            user.IsActive = false;
            user.LastSyncedDate = now;
        }

        var result = new UserSyncResult(added, updated, activeAbsent.Count);

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
                DeactivatedUserIds = activeAbsent.Select(u => u.UserId).ToArray(),
                AddedEntraObjectIds = dbContext.Users.Local.Where(u => u.UserId == 0).Select(u => u.EntraObjectId).ToArray(),
            },
            cancellationToken);

        _ = await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Entra user sync complete: {Added} added, {Updated} updated, {Deactivated} deactivated.",
            result.AddedCount,
            result.UpdatedCount,
            result.DeactivatedCount);

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
