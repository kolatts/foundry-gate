using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/keys</c> — APIM subscription-key lifecycle (spec &#167;4.5; issues #36, #37). The
/// <c>/me</c> routes serve any authenticated developer with a <c>User</c> row (403 otherwise — call
/// <c>GET /users/me</c> first); the <c>/{userId}</c> routes are admin-only. Every route that mints a
/// key returns the plaintext exactly once in an <see cref="ApiKeyRevealResponse"/>; every other read
/// is the masked <see cref="ApiKeyResponse"/>.
/// </summary>
public sealed class KeysController(IApimKeyService keys, IUserLifecycleService lifecycle) : ApiControllerBase
{
    /// <summary>The caller's key, masked to its last four characters; <c>isProvisioned = false</c> when they have none.</summary>
    [HttpGet("me")]
    [ProducesResponseType<ApiKeyResponse>(StatusCodes.Status200OK)]
    public Task<ApiKeyResponse> GetMineAsync(CancellationToken cancellationToken) =>
        keys.GetMineAsync(cancellationToken);

    /// <summary>
    /// Decrypts and returns the caller's full key once (spec &#167;11: fetched directly, never stored in
    /// the browser). Audited as <c>key.revealed</c>. 404 when the caller has no key.
    /// </summary>
    /// <remarks>Not yet rate-limited — a leaked bearer token can call this repeatedly; #136 adds a per-user limiter.</remarks>
    [HttpPost("me/reveal")]
    [ProducesResponseType<ApiKeyRevealResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ApiKeyRevealResponse> RevealMineAsync(CancellationToken cancellationToken) =>
        keys.RevealMineAsync(cancellationToken);

    /// <summary>
    /// Regenerates the caller's key — both the primary they hold and the never-issued secondary
    /// (#117) — and returns the new primary once. The old key stops working immediately. 404 when
    /// the caller has no key; 409 when the APIM subscription behind it has vanished.
    /// </summary>
    [HttpPost("me/rotate")]
    [ProducesResponseType<ApiKeyRevealResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ApiKeyRevealResponse> RotateMineAsync(CancellationToken cancellationToken) =>
        keys.RotateMineAsync(cancellationToken);

    /// <summary>
    /// Admin: provisions a key for an active user who has none, under the quota-tier product their
    /// <em>resolved</em> quota maps to (#118). Returns the plaintext once. 404 unknown user; 409 when
    /// the user already has a key or is deactivated.
    /// </summary>
    /// <remarks>
    /// There is no <c>?tier=</c> parameter (removed in the #156 review): a monthly budget <em>is</em> a
    /// gateway tier, so the product comes from the user's allocation and nothing else. To mint a key on
    /// a different tier, set the user's quota first (<c>PUT /users/{id}/quota</c>) — that moves the
    /// gateway as well, so the database and the gateway can never disagree about which budget is being
    /// enforced. Runs the full provision pipeline (<c>IUserLifecycleService</c>, plan 21 Trigger B).
    /// </remarks>
    [HttpPost("{userId:int}/provision")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<ApiKeyRevealResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ApiKeyRevealResponse> ProvisionAsync(int userId, CancellationToken cancellationToken) =>
        lifecycle.ProvisionKeyForUserAsync(userId, cancellationToken);

    /// <summary>Admin: rotates any user's key (both APIM keys regenerated, #117) and returns the new primary once. 404 unknown user or no key.</summary>
    [HttpPost("{userId:int}/rotate")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<ApiKeyRevealResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ApiKeyRevealResponse> RotateAsync(int userId, CancellationToken cancellationToken) =>
        keys.RotateForUserAsync(userId, cancellationToken);

    /// <summary>
    /// Admin: key-only revocation (#116) — deletes the APIM subscription and clears the stored key;
    /// the user stays active and can be re-provisioned. Idempotent: 204 whether or not a key existed.
    /// Deactivating the user is <c>POST /users/{id}/deactivate</c>, not this. 404 unknown user.
    /// </summary>
    [HttpDelete("{userId:int}")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeAsync(int userId, CancellationToken cancellationToken)
    {
        _ = await keys.RevokeForUserAsync(userId, cancellationToken);
        return NoContent();
    }
}
