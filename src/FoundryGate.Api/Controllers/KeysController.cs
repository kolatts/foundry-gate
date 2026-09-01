using FoundryGate.Api.Services.Keys;
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
public sealed class KeysController(IApimKeyService keys) : ApiControllerBase
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
    /// Admin: provisions a key for a user who has none, under the quota-tier product
    /// <paramref name="tier"/> (<c>standard</c> | <c>power</c> | <c>unlimited</c>; defaults to
    /// <see cref="GatewayTiers.Default"/>). Returns the plaintext once. 404 unknown user; 409 when the
    /// user already has a key or is deactivated; 400 for an unknown tier.
    /// </summary>
    /// <remarks>
    /// The tier is caller-supplied for now. #118 (<c>ApimGatewayTierSync</c>) wires the quota wave's
    /// resolution (#32/#33) to <c>IApimKeyService.MoveToProductAsync</c>, after which the user's
    /// <em>resolved</em> tier drives the product and this parameter becomes an override at most; the
    /// lifecycle orchestrator (epic #64) then owns the provision call itself.
    /// </remarks>
    [HttpPost("{userId:int}/provision")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<ApiKeyRevealResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ApiKeyRevealResponse> ProvisionAsync(int userId, [FromQuery] string tier = GatewayTiers.Default, CancellationToken cancellationToken = default) =>
        keys.ProvisionForUserAsync(userId, tier, cancellationToken);

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
