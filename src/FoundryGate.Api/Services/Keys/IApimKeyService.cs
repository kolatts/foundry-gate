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
/// that needs several of these to be atomic wraps them in a database transaction; the saves inside
/// then join it.
/// </para>
/// <para>
/// <b>Endpoint entry points</b> (<c>…Mine…</c> / <c>…ForUser…</c>) resolve the user — the caller via
/// <c>ICurrentUserAccessor</c> (403 when unprovisioned), or by <c>UserId</c> (404) — and delegate,
/// so <c>KeysController</c> stays an expression-bodied delegation.
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
    /// returns the plaintext once. If a subscription with that name already exists in APIM (an
    /// orphan left by a save that failed after APIM succeeded — plan 21's compensation table) it is
    /// reused: re-scoped to the requested tier if needed and both keys regenerated, so whatever key
    /// the orphan held is dead and the returned one is fresh.
    /// </summary>
    /// <exception cref="ConflictException"><paramref name="user"/> already has a key (<c>ApimSubscriptionId</c> set) → 409.</exception>
    /// <exception cref="ArgumentException"><paramref name="tierProductId"/> is not a <c>GatewayTiers</c> value → 400.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="user"/> has not been saved (<c>UserId</c> is 0) — a programming error, not a caller error.</exception>
    Task<ApiKeyRevealResponse> ProvisionAsync(User user, string tierProductId, CancellationToken cancellationToken);

    /// <summary>
    /// Regenerates <em>both</em> APIM keys (primary and secondary, #117), stores the new encrypted
    /// primary, audits <c>key.rotated</c>, saves, and returns the new plaintext once. The old primary
    /// is rejected by the gateway immediately.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="user"/> has no key → 404.</exception>
    /// <exception cref="ConflictException">The APIM subscription behind the key no longer exists → 409 with "revoke and re-provision" guidance.</exception>
    Task<ApiKeyRevealResponse> RotateAsync(User user, CancellationToken cancellationToken);

    /// <summary>
    /// Key-only revocation (#116 ruling; plan 21 deprovision step 1 + 5): deletes the APIM
    /// subscription, clears <c>ApimSubscriptionId/Key/KeyHint/KeyIssuedDate</c>, audits
    /// <c>key.revoked</c>, saves. <c>IsActive</c> is untouched — deactivation is
    /// <c>POST /users/{id}/deactivate</c>'s job, which calls this as its first step. Idempotent: a
    /// user with no key is a no-op that returns <see langword="false"/> (nothing audited); a
    /// subscription already gone from APIM still clears the row and audits.
    /// </summary>
    /// <returns><see langword="true"/> when a key was revoked.</returns>
    Task<bool> RevokeAsync(User user, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the subscription to another quota-tier product (#82: tier change = re-scope). Keys are
    /// unchanged; the gateway's monthly counter is per subscription-within-product, so the developer's
    /// used-so-far restarts under the new tier. Audits <c>key.tier-changed</c> with before/after
    /// product ids. Called by the quota wave when a user's resolved tier changes (#64).
    /// </summary>
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

    /// <summary><c>POST /keys/{userId}/provision</c> (admin): <see cref="ProvisionAsync"/> for <paramref name="userId"/>.</summary>
    /// <exception cref="KeyNotFoundException">No such user → 404.</exception>
    /// <exception cref="ConflictException">The user is deactivated (re-activate them instead — plan 21 Trigger C) or already has a key → 409.</exception>
    Task<ApiKeyRevealResponse> ProvisionForUserAsync(int userId, string tierProductId, CancellationToken cancellationToken);

    /// <summary><c>POST /keys/{userId}/rotate</c> (admin): <see cref="RotateAsync"/> for <paramref name="userId"/>.</summary>
    /// <exception cref="KeyNotFoundException">No such user, or the user has no key → 404.</exception>
    Task<ApiKeyRevealResponse> RotateForUserAsync(int userId, CancellationToken cancellationToken);

    /// <summary><c>DELETE /keys/{userId}</c> (admin): <see cref="RevokeAsync"/> for <paramref name="userId"/>.</summary>
    /// <exception cref="KeyNotFoundException">No such user → 404 (a user with no key is a successful no-op).</exception>
    Task<bool> RevokeForUserAsync(int userId, CancellationToken cancellationToken);
}
