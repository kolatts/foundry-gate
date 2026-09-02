using FoundryGate.Domain.Groups;
using FoundryGate.Domain.Groups.Contracts;

namespace FoundryGate.Api.Services.Groups;

/// <summary>
/// Pulls the membership of a FoundryGate <c>Group</c> from the Entra group it is linked to
/// (spec &#167;7.3; issue #41). Pull-only, idempotent, safe to run on a schedule or by hand: a second
/// run with nothing changed in the directory reports zero added and zero removed.
/// </summary>
/// <remarks>
/// <para>
/// Per group, in one unit of work (one <c>SaveChangesAsync</c>, one <c>group.entra-synced</c> audit
/// row attributed to the caller):
/// </para>
/// <list type="bullet">
/// <item><b>In the Entra group, not in this one</b> → a <c>GroupMember</c> row with
/// <c>AddedByUserId = null</c>. Null is the system actor: nobody chose this membership, the directory
/// did, and inventing the calling admin as its author would make the audit trail lie about who owns
/// it. <c>GET /groups/{id}/members</c> surfaces the null so the UI can label it "from Entra".</item>
/// <item><b>In this group, no longer in the Entra group</b> → the membership row is deleted. The
/// <c>User</c> is never touched — leaving a synced group is not leaving the company.</item>
/// <item><b>In the Entra group with no FoundryGate <c>User</c> row</b> → skipped, never invented, and
/// counted in <see cref="GroupSyncResult.SkippedUnknownUserCount"/> (plus a Warning log). These are
/// people who have never signed in and were not imported by <c>POST /users/sync</c>; run that first
/// if the number is not zero.</item>
/// </list>
/// <para>
/// Every <b>active</b> user whose membership changed is re-resolved for the current billing period
/// (<c>IQuotaResolutionService.ResolveManyAsync</c>), which is what moves their APIM tier product when
/// the group's policy differs from what they had. Inactive users are skipped for the same reason
/// <see cref="IGroupService"/> skips them.
/// </para>
/// <para>
/// Members are read transitively (<c>transitiveMembers</c>): an Entra group that contains other groups
/// grants FoundryGate membership to the people inside them, which is what an admin who nests
/// <c>SG_AI_Developers</c> under <c>SG_Engineering</c> means. Non-user members (devices, service
/// principals) are filtered out by <see cref="Entra.IEntraDirectoryClient"/>.
/// </para>
/// </remarks>
public interface IEntraGroupSyncService
{
    /// <summary>Reconciles one group against its linked Entra group.</summary>
    /// <exception cref="KeyNotFoundException">No such group (→ 404).</exception>
    /// <exception cref="ArgumentException">The group has no <c>EntraGroupId</c> — there is nothing to sync it against (→ 400).</exception>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (→ 403; call <c>GET /users/me</c> first).</exception>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">
    /// Entra is disabled on this host (<c>Entra:Enabled</c> is false) → 503; the message names the
    /// setting and the Graph roles to grant. A 503 rather than a 400 because nothing about the request
    /// is wrong — the host has not been configured for the feature.
    /// </exception>
    Task<GroupSyncResult> SyncAsync(int groupId, CancellationToken cancellationToken);

    /// <summary>
    /// Reconciles every group that has an <c>EntraGroupId</c>, in group-id order, one unit of work
    /// each. Groups without a link are not touched and do not appear in the result.
    /// <para>
    /// <b>Failures are isolated per group</b> (#149), and the two kinds are told apart
    /// (<see cref="GroupSyncErrorType"/>). A group whose reconciliation throws is left as it was and
    /// the run continues; its summary carries <c>Succeeded = false</c> with the failure's message in
    /// <c>Error</c> and zeroes everywhere else, while every other group still reports its real counts.
    /// The call answers <c>200</c> with the partial results rather than a <c>500</c> that says nothing
    /// about what did happen.
    /// <list type="bullet">
    /// <item><see cref="GroupSyncErrorType.GraphRead"/> — the failure landed before anything outside
    /// the database was touched. Its pending changes are discarded so they cannot ride along on the
    /// next group's save, it is logged at Warning, and re-running once the cause is fixed is
    /// sufficient (the sync is idempotent).</item>
    /// <item><see cref="GroupSyncErrorType.PostCommit"/> — the gateway had already accepted a member's
    /// tier move and the database write failed anyway, including the retry. Logged at <b>Error</b>
    /// with the group's full identity, plus an Error summary line for the run, because the gateway and
    /// the control plane now disagree and a person has to know.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The <c>Users</c> table is read <b>once for the run</b> and shared across the loop, not once per
    /// group. Nothing in this path inserts a user, so a single snapshot cannot go stale mid-run.
    /// </para>
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (→ 403).</exception>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">
    /// Entra is disabled on this host and at least one group is linked (→ 503). Deliberately <em>not</em>
    /// isolated per group: every group would carry the same message, and the host — not any one group —
    /// is what needs fixing.
    /// </exception>
    Task<IReadOnlyList<GroupSyncResult>> SyncAllAsync(CancellationToken cancellationToken);
}
