using FoundryGate.Data.Entities;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys.Contracts;

namespace FoundryGate.Api.Services.Lifecycle;

/// <summary>
/// <para>
/// The single orchestrator for plan 21's provision and deprovision pipelines (#64/#65/#66). Every
/// lifecycle trigger — a developer's first login, an admin's provision/activate/deactivate, the Entra
/// sync spotting a departure — runs the <em>same</em> sequence here, so no controller and no sync job
/// re-implements "and don't forget to hard-stop the allocation".
/// </para>
/// <para><b>Provision</b> (<see cref="ProvisionAsync"/>): [create the <c>User</c> — first login only] →
/// resolve this period's quota → mint the APIM subscription under the resolved tier product → audit →
/// save. <b>Deprovision</b> (<see cref="DeprovisionAsync"/>): delete the APIM subscription and clear the
/// key → <c>IsActive = false</c> → hard-stop this period's allocation → reject pending quota-increase
/// requests → audit → save.</para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicity.</b> Each call wraps its database work in one transaction — its own, or the caller's
/// when one is already open (<c>EntraUserSyncService</c> opens one so a whole sync run commits at once).
/// <c>IApimKeyService</c>'s building blocks join that transaction rather than opening their own (they
/// check <c>Database.CurrentTransaction</c>), so their claim/save participate in it. The APIM call
/// itself is not transactional: it happens inside the transaction's lifetime, and a failure rolls the
/// database back — a first login that cannot reach APIM leaves <em>no</em> <c>User</c> row, which is
/// exactly plan 21's compensation for step 4a. The reverse residue (APIM created the subscription, the
/// commit then failed) is deliberate and safe: the subscription is left orphaned under the
/// <c>foundrygate-{UserId}</c> name and the next provision adopts it, regenerating both keys (#66).
/// </para>
/// <para>
/// <b>Failure shape.</b> APIM not configured on this host → <see cref="FeatureNotConfiguredException"/>
/// (503, the operator's problem). APIM configured but failing → <see cref="UpstreamDependencyException"/>
/// (502, retryable). A caller-caused refusal keeps its own status: 409 for a state conflict, 404 for an
/// unknown user, 403 for a caller with no <c>User</c> row of their own to attribute the audit to.
/// </para>
/// </remarks>
public interface IUserLifecycleService
{
    /// <summary>
    /// Runs the provision pipeline for <paramref name="trigger"/> and returns the tracked, saved
    /// <see cref="User"/>.
    /// </summary>
    /// <param name="trigger">Which of plan 21's three provision triggers is running.</param>
    /// <param name="context">
    /// The user to provision — <see cref="ProvisionContext.FirstLogin"/> for
    /// <see cref="ProvisionTrigger.FirstLogin"/> (the row is created from the caller's claims),
    /// <see cref="ProvisionContext.ForUser"/> otherwise.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provisioned user, tracked by the request's context, with its key fields populated.</returns>
    /// <exception cref="ArgumentException"><paramref name="context"/> names no user for a trigger that needs one (→ 400).</exception>
    /// <exception cref="KeyNotFoundException">The named user does not exist (→ 404).</exception>
    /// <exception cref="ConflictException">
    /// The user is already in the target state — <see cref="ProvisionTrigger.Reactivate"/> on an active
    /// user, <see cref="ProvisionTrigger.AdminProvision"/> on a deactivated one or one that already holds
    /// a key — or two first logins for the same oid raced (→ 409).
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row to attribute the audit to (→ 403).</exception>
    /// <exception cref="FeatureNotConfiguredException">APIM key management is not configured on this host (→ 503); nothing was persisted.</exception>
    /// <exception cref="UpstreamDependencyException">APIM (or, on first login, Microsoft Graph) failed (→ 502); nothing was persisted.</exception>
    Task<User> ProvisionAsync(ProvisionTrigger trigger, ProvisionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /keys/{userId}/provision</c> (admin): the <see cref="ProvisionTrigger.AdminProvision"/>
    /// pipeline for an existing, active user who holds no key, returning the plaintext once.
    /// </summary>
    /// <remarks>
    /// This entry point lives here rather than on <c>IApimKeyService</c> because the tier a key is minted
    /// under is the user's <em>resolved</em> tier, and resolution sits above the key service (which the
    /// tier sync depends on, so the key service cannot depend back on it). There is deliberately no tier
    /// parameter: a budget <em>is</em> a tier, so the way to mint a key on a different product is to set
    /// the user's quota (<c>PUT /users/{id}/quota</c>) — which moves the gateway too. A caller-supplied
    /// tier could disagree with the allocation the database records, which is the exact drift the tier
    /// sync exists to prevent.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No such user (→ 404).</exception>
    /// <exception cref="ConflictException">The user is deactivated (re-activate them instead) or already holds a key (→ 409).</exception>
    /// <exception cref="FeatureNotConfiguredException">APIM key management is not configured on this host (→ 503).</exception>
    /// <exception cref="UpstreamDependencyException">APIM failed (→ 502); nothing was persisted.</exception>
    Task<ApiKeyRevealResponse> ProvisionKeyForUserAsync(int userId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the deprovision pipeline for <paramref name="trigger"/> against <paramref name="userId"/>:
    /// the APIM subscription is <b>deleted</b> (there is no suspended state, #116), the user is
    /// deactivated, their current-period allocation is hard-stopped, and every Pending
    /// <c>QuotaIncreaseRequest</c> of theirs is rejected with a system note.
    /// </summary>
    /// <param name="trigger">Which of plan 21's deprovision triggers is running; decides audit attribution and whether an already-inactive user is a conflict.</param>
    /// <param name="userId">The user to deprovision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">No such user (→ 404).</exception>
    /// <exception cref="ConflictException">The user is already deactivated and the trigger is <see cref="DeprovisionTrigger.AdminDeactivation"/> (→ 409). <see cref="DeprovisionTrigger.EntraDeparture"/> is idempotent instead.</exception>
    /// <exception cref="UnauthorizedAccessException">The calling admin has no <c>User</c> row to attribute the audit to (→ 403). Never thrown for <see cref="DeprovisionTrigger.EntraDeparture"/>, which audits as the system.</exception>
    /// <exception cref="UpstreamDependencyException">APIM failed to delete the subscription (→ 502); nothing was persisted.</exception>
    Task DeprovisionAsync(DeprovisionTrigger trigger, int userId, CancellationToken cancellationToken);
}
