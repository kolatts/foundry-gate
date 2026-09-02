using FoundryGate.Data.Entities;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys.Contracts;

namespace FoundryGate.Api.Services.Keys;

/// <summary>
/// <para>
/// APIM subscription-key lifecycle for a developer (spec &#167;4.5, &#167;5.1–5.3; issues #36, #37;
/// plans/09 and 21). Two layers in one interface:
/// </para>
/// <para>
/// <b>Building blocks</b> take a tracked <see cref="User"/> and are what plan 21's
/// <c>IUserLifecycleService</c> (#65) composes into the provision/deprovision pipelines: each does
/// its APIM call(s), mutates the user, adds its audit row through <c>IAuditService</c>, and
/// <c>SaveChangesAsync</c> — mutation and audit commit together (CONVENTIONS.md). An orchestrator
/// that needs several of these to be atomic opens a database transaction first; the saves (and
/// <see cref="ProvisionAsync"/>'s claim) then join it instead of opening their own.
/// </para>
/// <para>
/// <b>Endpoint entry points</b> (<c>…Mine…</c> / <c>…ForUser…</c>) resolve the user — the caller via
/// <c>ICurrentUserAccessor</c> (403 when unprovisioned), or by <c>UserId</c> (404) — and delegate,
/// so <c>KeysController</c> stays an expression-bodied delegation.
/// </para>
/// <para>
/// Actor: every building block except <see cref="RevokeAsSystemAsync"/> attributes its audit row to
/// the current HTTP caller and resolves that caller <em>before</em> any APIM side effect (403 with no
/// orphan left behind). <see cref="RevokeAsSystemAsync"/> exists for the paths with no caller — plan
/// 21 deprovision Trigger B (Entra departure detected by a sync job) — and writes a system audit row.
/// </para>
/// <para>
/// Key material rules: the plaintext key crosses the API boundary exactly once per mint (in an
/// <see cref="ApiKeyRevealResponse"/>) or per explicit reveal; it is stored only through
/// <c>IKeyProtector</c>; it is never logged and never placed in an audit <c>details</c> payload. The
/// APIM secondary key is never issued or stored — rotation regenerates it too so its lifetime never
/// exceeds the primary's (#117).
/// </para>
/// </summary>
public interface IApimKeyService
{
    /// <summary>
    /// Mints the user's APIM subscription — <c>foundrygate-{UserId}</c>, display name carrying the
    /// email, scoped to <c>/products/{tierProductId}</c> — stores the encrypted primary key, resource
    /// id, hint and issue date on <paramref name="user"/>, audits <c>key.provisioned</c>, saves, and
    /// returns the plaintext once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Concurrency:</b> before calling APIM the row is <em>claimed</em> with a conditional update
    /// (<c>ApimSubscriptionId = '' → resource id</c>) inside a transaction. A second provisioner for the
    /// same user therefore fails the claim (409) instead of issuing a second PUT that would regenerate
    /// — and kill — the first caller's key. If APIM then fails, the transaction rolls the claim back;
    /// if the process dies after APIM succeeded but before the commit, the claim rolls back too and
    /// the subscription is left as an orphan for the next provision to adopt.
    /// </para>
    /// <para>
    /// <b>Orphans</b> (plan 21's compensation table): a subscription that already exists under this
    /// name is reused — re-scoped to the requested tier if needed and <em>both keys regenerated</em>, so
    /// whatever key it held is dead and the returned one is fresh. An orphan whose state is not
    /// <c>active</c> (suspended or hand-made) is deleted and created afresh instead, because a
    /// regenerated key on a suspended subscription would still 401 at the gateway.
    /// </para>
    /// </remarks>
    /// <exception cref="ConflictException"><paramref name="user"/> already has a key, or another request is provisioning one right now → 409.</exception>
    /// <exception cref="ArgumentException"><paramref name="tierProductId"/> is not a <c>GatewayTiers</c> value → 400.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="user"/> has not been saved (<c>UserId</c> is 0) — a programming error, not a caller error.</exception>
    Task<ApiKeyRevealResponse> ProvisionAsync(User user, string tierProductId, CancellationToken cancellationToken);

    /// <summary>
    /// Regenerates <em>both</em> APIM keys (primary and secondary, #117), stores the new encrypted
    /// primary, audits <c>key.rotated</c>, saves, and returns the new plaintext once. The old primary
    /// is rejected by the gateway immediately. If storing the new key fails after APIM regenerated it,
    /// the previous (now stale) ciphertext is kept, an error is logged and a <c>key.rotation-failed</c>
    /// audit row records the remedy: rotate again, or revoke and re-provision.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="user"/> has no key → 404.</exception>
    /// <exception cref="ConflictException">The APIM subscription behind the key no longer exists → 409 with "revoke and re-provision" guidance.</exception>
    Task<ApiKeyRevealResponse> RotateAsync(User user, CancellationToken cancellationToken);

    /// <summary>
    /// Key-only revocation (#116 ruling; plan 21 deprovision step 1 + 5): deletes the APIM
    /// subscription, clears <c>ApimSubscriptionId/Key/KeyHint/KeyIssuedDate</c>, audits
    /// <c>key.revoked</c> attributed to the current caller, saves. <c>IsActive</c> is untouched —
    /// deactivation is <c>POST /users/{id}/deactivate</c>'s job, which calls this as its first step.
    /// Idempotent: a user with no key is a no-op that returns <see langword="false"/> (nothing
    /// audited); a subscription already gone from APIM still clears the row and audits.
    /// </summary>
    /// <returns><see langword="true"/> when a key was revoked.</returns>
    /// <exception cref="UnauthorizedAccessException">No current caller with a <c>User</c> row → 403. Use <see cref="RevokeAsSystemAsync"/> from jobs.</exception>
    Task<bool> RevokeAsync(User user, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="RevokeAsync"/> for code paths with no HTTP caller — plan 21 deprovision Trigger B, the
    /// Entra sync detecting a departed user (#40/#65) — attributing the <c>key.revoked</c> row to the
    /// system (<c>ActorUserId = null</c>, via <c>IAuditWriter.AddSystem</c>) with
    /// <paramref name="reason"/> in its details. Never resolves <c>ICurrentUserAccessor</c>, so it is
    /// safe from a background job or a request whose principal is not a FoundryGate user.
    /// </summary>
    /// <param name="user">The tracked user whose key is revoked.</param>
    /// <param name="reason">Why the system revoked it (e.g. <c>"entra-departure"</c>); stored in the audit details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when a key was revoked.</returns>
    Task<bool> RevokeAsSystemAsync(User user, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the subscription to another quota-tier product (#82: tier change = re-scope). Keys are
    /// unchanged; the gateway's monthly counter is per subscription-within-product, so the developer's
    /// used-so-far restarts under the new tier. Adds a <c>key.tier-changed</c> row with before/after
    /// product ids. Called by the quota wave when a user's resolved tier changes (#118).
    /// </summary>
    /// <remarks>
    /// <b>Does not save</b> (unlike every other building block here). Quota resolution calls this in the
    /// middle of its caller's unit of work — after the caller has already mutated a quota and before it
    /// has written its own audit row — so a save here would commit that half-finished change. The
    /// <c>key.tier-changed</c> row goes on the shared change tracker and commits with the caller's own
    /// <c>SaveChangesAsync</c>, which (because APIM has already been re-scoped by then) must run on
    /// <see cref="CancellationToken.None"/>. A caller that does not save is a bug: the gateway would be
    /// enforcing a tier the audit trail never recorded.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="user"/> has no key → 404.</exception>
    /// <exception cref="ArgumentException"><paramref name="tierProductId"/> is not a <c>GatewayTiers</c> value → 400.</exception>
    /// <exception cref="ConflictException">The APIM subscription behind the key no longer exists → 409.</exception>
    Task MoveToProductAsync(User user, string tierProductId, CancellationToken cancellationToken);

    /// <summary>
    /// The masked view (<c>••••••••1a2b</c>) from the stored hint — no decryption, no Key Vault
    /// round trip, so profile reads stay cheap. <c>IsProvisioned = false</c> with null fields when the
    /// user has no key.
    /// </summary>
    ApiKeyResponse GetMasked(User user);

    /// <summary>
    /// Decrypts and returns the full key (spec &#167;11: "reveal action fetches directly, not stored in
    /// browser"). Audits <c>key.revealed</c> and saves that row; nothing is cached.
    /// <c>IssuedDate</c> is when the key was minted or last rotated, never the reveal time.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="user"/> has no key → 404.</exception>
    Task<ApiKeyRevealResponse> RevealAsync(User user, CancellationToken cancellationToken);

    /// <summary><c>GET /keys/me</c>: <see cref="GetMasked"/> for the caller.</summary>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row → 403 ("call GET /users/me first").</exception>
    Task<ApiKeyResponse> GetMineAsync(CancellationToken cancellationToken);

    /// <summary><c>POST /keys/me/reveal</c>: <see cref="RevealAsync"/> for the caller.</summary>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row, or is deactivated → 403.</exception>
    Task<ApiKeyRevealResponse> RevealMineAsync(CancellationToken cancellationToken);

    /// <summary><c>POST /keys/me/rotate</c>: <see cref="RotateAsync"/> for the caller.</summary>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row, or is deactivated → 403.</exception>
    Task<ApiKeyRevealResponse> RotateMineAsync(CancellationToken cancellationToken);

    // POST /keys/{userId}/provision has no entry point here on purpose: the tier a key is minted under
    // is the user's *resolved* tier, and resolution lives above this service (IUserLifecycleService,
    // #64/#118). The endpoint calls IUserLifecycleService.ProvisionKeyForUserAsync instead.

    /// <summary><c>POST /keys/{userId}/rotate</c> (admin): <see cref="RotateAsync"/> for <paramref name="userId"/>.</summary>
    /// <exception cref="KeyNotFoundException">No such user, or the user has no key → 404.</exception>
    Task<ApiKeyRevealResponse> RotateForUserAsync(int userId, CancellationToken cancellationToken);

    /// <summary><c>DELETE /keys/{userId}</c> (admin): <see cref="RevokeAsync"/> for <paramref name="userId"/>.</summary>
    /// <exception cref="KeyNotFoundException">No such user → 404 (a user with no key is a successful no-op).</exception>
    Task<bool> RevokeForUserAsync(int userId, CancellationToken cancellationToken);
}
