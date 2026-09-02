using System.Globalization;
using System.Text.Json;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Data;
using FoundryGate.Data.Concurrency;
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

    /// <summary>
    /// How <c>LastUserSyncResult</c> is written and read (#171). Web defaults, so the stored JSON is
    /// camelCase — the same shape the endpoint puts on the wire, which makes a stored row readable by
    /// anyone who has seen a <c>POST /users/sync</c> response.
    /// </summary>
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<UserSyncResult> SyncUsersAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Tracked on purpose: matched rows are updated in place and saved below. EntraObjectId is
        // unique, so the dictionary cannot collide. Case-insensitive because oids are GUID strings
        // whose casing varies by source (token claims vs. Graph vs. hand-seeded rows).
        var existingByOid = await dbContext.Users
            .ToDictionaryAsync(u => u.EntraObjectId, StringComparer.OrdinalIgnoreCase, cancellationToken);

        // The whole-org Graph enumeration happens before any transaction is open and before anything is
        // mutated, so a paged read of thousands of principals never sits inside a write lock (#156 review).
        var assigned = await directory.ListAssignedUsersAsync(cancellationToken);

        // First pass decides only *who* is who; nothing is mutated yet, because the departures below run
        // in their own units of work and must not flush half-applied adds into one of their transactions.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toApply = new List<EntraUser>();

        foreach (var entraUser in assigned.Users)
        {
            if (seen.Add(entraUser.ObjectId))
            {
                toApply.Add(entraUser); // a principal listed twice must count once
            }
        }

        var activeAbsent = existingByOid.Values
            .Where(u => u.IsActive && !seen.Contains(u.EntraObjectId))
            .OrderBy(u => u.UserId)
            .ToList();

        var skippedGroups = assigned.SkippedGroupAssignments;
        var deactivated = new List<User>();
        var failed = 0;
        if (skippedGroups.Count > 0)
        {
            // Group assignees are expanded to their members now (#121), so this list is no longer "every
            // group" — it is the groups whose expansion FAILED, which leaves a partial view of the
            // population. An active user missing from the user list may simply be covered by one of
            // them, so departure detection is suspended for the run rather than flipping everyone to
            // inactive and deleting every APIM subscription — on a 200 with a clean audit row. Fix the
            // Graph permission (or the deleted group) and the next run deactivates normally.
            logger.LogWarning(
                "Entra user sync skipped departure detection: {GroupCount} group app-role assignment(s) could not be expanded to their members. {ActiveAbsentCount} active user(s) absent from the assigned-user list were left active. Groups: {Groups}",
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
            foreach (var user in activeAbsent)
            {
                // Plan 21 deprovision Trigger B, in full (#65): the APIM subscription is deleted, the
                // current allocation hard-stopped and pending increase requests rejected — not just a
                // flag flip that would leave a departed employee holding a working gateway key. No HTTP
                // caller is involved, so its audit rows are system-attributed.
                //
                // One unit of work per departure (#156 review). An APIM DELETE cannot be rolled back, so
                // spanning N of them in a single transaction meant a 502 on the last one erased the
                // database record of every earlier deletion. Each user now commits on its own and a
                // failure is counted, logged and skipped — the rest of the run still lands, and the next
                // run retries the failures (RevokeAsync is idempotent on a missing subscription).
                user.LastSyncedDate = now;
                try
                {
                    await lifecycle.DeprovisionAsync(DeprovisionTrigger.EntraDeparture, user.UserId, cancellationToken);
                    deactivated.Add(user);
                }
                catch (UpstreamDependencyException exception)
                {
                    failed++;
                    logger.LogError(
                        exception,
                        "Entra user sync could not deprovision departed user {UserId} ({EntraObjectId}); the run continues and the next one will retry.",
                        user.UserId,
                        user.EntraObjectId);
                }
            }
        }

        // Adds and updates only after the departures, so nothing half-applied is pending while a
        // deprovision saves. The one SaveChangesAsync below commits them with the users.synced row.
        var addedOids = new List<string>();
        var updated = 0;
        foreach (var entraUser in toApply)
        {
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

        var result = new UserSyncResult(addedOids.Count, updated, deactivated.Count, skippedGroups.Count, failed);

        // ---- commit point (CONVENTIONS.md): a deactivation deleted an APIM subscription, which cannot
        // be rolled back, so from here a disconnecting client must not be able to abandon the record of
        // it. Everything below — the LastUserSync* rows, the audit row and the save — runs on this
        // token. A run that reached nobody's subscription is still an abandonable request, and stops.
        var completionToken = CommitToken.For(deactivated.Count > 0, cancellationToken);

        // #171: the durable record of this run, written into the same unit of work as the audit row
        // below so "the sync happened" and "here is when and what" can never disagree. The audit log
        // already has the history; these two rows are the *latest* run, which is the question
        // /users/sync asks on a cold load and cannot answer from a paged, filtered log.
        await RecordLastSyncAsync(now, result, completionToken);

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
                result.FailedCount,
                DeactivatedUserIds = deactivated.Select(u => u.UserId).ToArray(),
                AddedEntraObjectIds = addedOids.ToArray(),
                DepartureDetectionSuspended = skippedGroups.Count > 0,
                SkippedGroupAssignments = skippedGroups.Select(g => new { g.GroupObjectId, g.DisplayName }).ToArray(),
            },
            completionToken);

        // One save, so the adds/updates, the LastUserSync* rows and the users.synced row that describes
        // them commit together. No explicit transaction: SaveChangesAsync is already atomic, and the
        // departures above deliberately committed on their own (see the loop).
        _ = await dbContext.SaveChangesAsync(completionToken);

        logger.LogInformation(
            "Entra user sync complete: {Added} added, {Updated} updated, {Deactivated} deactivated, {Failed} failed, {SkippedGroups} group assignment(s) unexpanded.",
            result.AddedCount,
            result.UpdatedCount,
            result.DeactivatedCount,
            result.FailedCount,
            result.SkippedGroupAssignmentCount);

        return result;
    }

    /// <inheritdoc />
    public async Task<UserSyncStatusResponse> GetLastSyncStatusAsync(CancellationToken cancellationToken)
    {
        // The whole table (seven rows on a shipped fork) and matched in memory, so the key comparison
        // cannot depend on the provider's collation — a `Where(c => c.Key == …)` would have done the
        // matching in SQL, where it is case-insensitive under SQL Server's default collation and
        // case-sensitive under the SQLite the tests run on. ConfigService materializes it for exactly
        // the same reason. Never tracked: nothing here writes.
        var rows = await dbContext.SystemConfigurations
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new UserSyncStatusResponse(
            ParseDate(Value(rows, SystemConfigurationKeys.LastUserSyncDate)),
            ParseResult(Value(rows, SystemConfigurationKeys.LastUserSyncResult)));
    }

    /// <summary>
    /// Stamps the two <c>LastUserSync*</c> rows. Nothing is saved here — the caller's single
    /// <c>SaveChangesAsync</c> commits them with the adds, the updates and the <c>users.synced</c>
    /// audit row. A fork whose database predates #171 has no rows to update, so they are inserted:
    /// the reference-data seeder would add them on the next deploy, and a sync run before then should
    /// still be remembered.
    /// </summary>
    private async Task RecordLastSyncAsync(DateTimeOffset now, UserSyncResult result, CancellationToken cancellationToken)
    {
        // Tracked, and matched in memory for the same collation reason as the read above — one query
        // for both rows rather than a keyed lookup each.
        var rows = await dbContext.SystemConfigurations.ToListAsync(cancellationToken);

        Set(rows, SystemConfigurationKeys.LastUserSyncDate, now.ToString("O", CultureInfo.InvariantCulture));
        Set(rows, SystemConfigurationKeys.LastUserSyncResult, JsonSerializer.Serialize(result, ResultJson));
    }

    private void Set(List<SystemConfiguration> rows, string key, string value)
    {
        var row = rows.Find(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new SystemConfiguration { Key = key };
            _ = dbContext.SystemConfigurations.Add(row);
        }

        row.Value = value;

        // UpdatedByUserId stays null on purpose: nobody edited this row. The admin who triggered the
        // run is on the users.synced audit row, which is where "who did this" belongs.
        row.UpdatedByUserId = null;
    }

    private static string? Value(List<SystemConfiguration> rows, string key) =>
        rows.Find(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    /// <summary>A stored result that cannot be read is "no result", never an exception: it is a souvenir of a past run, not state anything depends on.</summary>
    private static UserSyncResult? ParseResult(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UserSyncResult>(value, ResultJson);
        }
        catch (JsonException)
        {
            return null;
        }
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
