namespace FoundryGate.Domain.Groups;

/// <summary>
/// Why one group of a <c>POST /groups/sync-entra</c> run failed. The distinction is not cosmetic: one
/// of these values means "nothing happened, re-run when the cause is fixed" and the other means "the
/// gateway already changed and the database did not", which needs a person to look.
/// </summary>
public enum GroupSyncErrorType
{
    /// <summary>The group reconciled; <c>GroupSyncResult.Error</c> is <see langword="null"/>.</summary>
    None = 0,

    /// <summary>
    /// The failure happened <em>before</em> anything outside the database was touched — a Graph read
    /// that was refused or unreachable, or a group that no longer exists. Nothing was applied
    /// anywhere, the group's pending changes were discarded, and re-running once the cause is fixed
    /// is both safe and sufficient.
    /// </summary>
    GraphRead = 1,

    /// <summary>
    /// The APIM tier move was <b>accepted</b> and the database write that records it then failed —
    /// twice, since the save is retried once on <see cref="CancellationToken.None"/>.
    /// The gateway and the control plane disagree: a member is on a product the
    /// <c>QuotaAllocation</c> row does not name, and this group's membership rows were not written.
    /// Logged at Error with the group's full identity, the way every commit-point failure is
    /// (CONVENTIONS.md, "External side effects have a commit point"). Re-running the sync <em>does</em>
    /// converge the database — resolution is idempotent — but until someone does, the reported state
    /// for this group is not to be trusted.
    /// </summary>
    PostCommit = 2,
}
