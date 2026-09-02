using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Api.Services.Entra;

/// <summary>
/// Bulk Entra → <c>Users</c> reconciliation behind <c>POST /users/sync</c> (spec &#167;7.2, issue #40).
/// Pull-only, idempotent, safe to run on a schedule or by hand at any time.
/// </summary>
public interface IEntraUserSyncService
{
    /// <summary>
    /// Reconciles the <c>Users</c> table against the people assigned to the FoundryGate application
    /// in Entra, in one unit of work (one <c>SaveChangesAsync</c>, one <c>users.synced</c> audit row
    /// attributed to the caller):
    /// <list type="bullet">
    /// <item><b>In Entra, not in the table</b> → a new <c>User</c> with defaults (<c>IsActive = true</c>,
    /// no quota override, <b>no APIM key</b> — keys are only provisioned on first login or by an admin).</item>
    /// <item><b>In both</b> → <c>DisplayName</c>/<c>Email</c>/<c>EmployeeId</c> refreshed from the
    /// directory and <c>LastSyncedDate</c> stamped; counted as updated whether or not a field changed.
    /// <c>IsActive</c> is left alone — an admin-deactivated user who is still in Entra stays inactive,
    /// and a previously departed user who reappears is <em>not</em> auto-reactivated (plan #21:
    /// "only if the user returns to Entra <em>and an admin re-activates</em>").</item>
    /// <item><b>In the table, not in Entra</b> → <c>IsActive = false</c>. Rows are never deleted (audit
    /// history). Users already inactive are not counted again.</item>
    /// </list>
    /// <b>Group-assigned access suspends departure detection.</b> Until #121 expands group-principal
    /// app-role assignments to their members, a user who is assigned through a group is invisible to
    /// the sync, so "absent from the user list" cannot mean "departed". When the directory reports one
    /// or more such assignments the deactivation step is skipped entirely (<c>DeactivatedCount = 0</c>),
    /// adds and updates still happen, the groups are named in a warning log and in the audit row, and
    /// <c>UserSyncResult.SkippedGroupAssignmentCount</c> tells the admin why nobody was deactivated.
    /// </summary>
    /// <remarks>
    /// <b>Deprovision scope in this wave</b>: a departed user is only flagged inactive (plus the audit
    /// row). The full deprovision pipeline for the Entra-departure trigger — APIM subscription
    /// deletion, hard-stopping the current allocation, cancelling pending requests (plan #21,
    /// deprovision trigger B) — lands with <c>IUserLifecycleService</c> in issue <b>#65</b>, which
    /// replaces the flag-only branch here with <c>DeprovisionAsync(EntraDeparture, userId)</c>.
    /// </remarks>
    /// <returns>Counts of users added, updated and deactivated by this run.</returns>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">Entra sync is disabled on this host (<c>Entra:Enabled</c> is false) → 503.</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">The directory returned <em>no</em> assigned users while active users exist locally → 409; refusing to deactivate everyone on what is almost certainly a misconfiguration.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (and is not among the assigned users being imported) → 403.</exception>
    Task<UserSyncResult> SyncUsersAsync(CancellationToken cancellationToken);
}
