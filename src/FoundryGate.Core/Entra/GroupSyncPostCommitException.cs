namespace FoundryGate.Core.Entra;

/// <summary>
/// One group's reconciliation moved a member's APIM tier — the gateway <b>accepted</b> it — and the
/// database write that records the move then failed, including the one retry on
/// <c>CancellationToken.None</c>. The gateway and the control plane now disagree for that group.
/// </summary>
/// <remarks>
/// <para>
/// Internal to <see cref="EntraGroupSyncService"/>, and the reason it exists is the difference in what
/// a caller must do about it. A Graph read that failed applied nothing anywhere and is answered by
/// re-running; this applied something outside the database that the database does not know about, so
/// it is logged at Error with the group's full identity at the call site (CONVENTIONS.md, "External
/// side effects have a commit point") and reported as
/// <see cref="Domain.Groups.GroupSyncErrorType.PostCommit"/> rather than folded into the ordinary
/// per-group Warning.
/// </para>
/// <para>
/// Out of <c>SyncAsync</c> (one group) it propagates and the request fails loudly, matching
/// <c>FoundryDeploymentService.AuditAfterCommitAsync</c>. Out of <c>SyncAllAsync</c> it is caught, so
/// the remaining groups are still reconciled, but it never looks like a clean failure in the summary.
/// #163 tracks centralizing this pattern across every service that has a commit point.
/// </para>
/// </remarks>
internal sealed class GroupSyncPostCommitException : Exception
{
    public GroupSyncPostCommitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
