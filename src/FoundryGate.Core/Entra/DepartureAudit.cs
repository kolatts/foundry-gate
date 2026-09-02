namespace FoundryGate.Core.Entra;

/// <summary>
/// The three strings a departure's audit trail is made of, in one place because two hosts write them
/// (#151) and #214 has not yet made that one implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why constants rather than a comment.</b> The first version of
/// <see cref="DeprovisioningDepartureHandler"/> tied itself to the Api's literals with XML docs saying
/// "matches <c>UserLifecycleService.EntraDepartureReason</c>", and the <c>trigger</c> field — the one
/// field that says <em>why</em> the row exists — drifted anyway before the change had even merged: Core
/// wrote <c>"IDepartureHandler"</c> where the Api writes <c>"EntraDeparture"</c>, so an operator
/// filtering <c>details.trigger == "EntraDeparture"</c> would have seen every admin-found departure and
/// none of the nightly ones. The comment could not fail a build; these can, and
/// <c>DepartureHandlerParityTests</c> makes them.
/// </para>
/// <para>
/// The Api's <c>UserLifecycleService</c> defines its own <c>EntraDepartureReason</c> and
/// <c>DeactivationReviewNote</c> as aliases of these, so its call sites and tests read as they always
/// did while the values live here. Its <c>trigger</c> comes from <c>DeprovisionTrigger.ToString()</c>,
/// which has to keep covering <c>AdminDeactivation</c> too — so <see cref="Trigger"/> is pinned against
/// <c>nameof(DeprovisionTrigger.EntraDeparture)</c> by a test rather than by the compiler.
/// </para>
/// </remarks>
public static class DepartureAudit
{
    /// <summary>
    /// The <c>trigger</c> value in a departure's <c>user.deactivated</c> details — plan 21's
    /// deprovision Trigger B, whichever host found it. Equals <c>nameof(DeprovisionTrigger.EntraDeparture)</c>.
    /// </summary>
    public const string Trigger = "EntraDeparture";

    /// <summary>The <c>reason</c> recorded on the system-attributed <c>key.revoked</c> row.</summary>
    public const string KeyRevocationReason = "entra-departure";

    /// <summary>The <c>ReviewNotes</c> stamped on a Pending quota increase request the deprovision closes.</summary>
    public const string ReviewNote = "User deactivated";
}
