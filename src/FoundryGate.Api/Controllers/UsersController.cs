using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Users;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/users</c> (spec &#167;4.1). One developer-facing action — <c>GET /users/me</c>, which
/// auto-provisions the caller on first login (#28) — and the admin surface: list, detail, quota,
/// activate/deactivate (#29) and the Entra bulk sync (#40). Admin-only authorization is declared per
/// action rather than on the class, because <c>/me</c> is for everyone.
/// </summary>
public sealed class UsersController(IUserService users, IEntraUserSyncService entraUserSyncService) : ApiControllerBase
{
    /// <summary>
    /// The caller's own profile: identity, this month's quota gauge, their masked key, and the gateway
    /// connection details a CLI needs. <b>Auto-provisions on the first call</b> — creates the user from
    /// the token's Entra claims (enriched from the directory when <c>Entra:Enabled</c>), resolves this
    /// month's quota, and mints their APIM subscription, all atomically (plan 21). Every later call is
    /// idempotent.
    /// </summary>
    /// <response code="200">The caller's profile.</response>
    /// <response code="403">The caller's account is deactivated.</response>
    /// <response code="409">A concurrent first login for the same identity won the race; retry.</response>
    /// <response code="502">First login: the gateway (or Microsoft Graph) failed. Nothing was created; retry.</response>
    /// <response code="503">First login on a host where APIM key management is not configured.</response>
    [HttpGet("me")]
    [ProducesResponseType<UserProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public Task<UserProfileResponse> GetMeAsync(CancellationToken cancellationToken) =>
        users.GetMyProfileAsync(cancellationToken);

    /// <summary>
    /// Admin: users ordered by display name, paged. <c>?search=</c> matches a substring of the display
    /// name or email; <c>?isActive=</c> keeps only active or only deactivated users.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<PagedResult<UserResponse>>(StatusCodes.Status200OK)]
    public Task<PagedResult<UserResponse>> ListAsync(
        [FromQuery] UserListQuery filter,
        [FromQuery] PagedRequest paging,
        CancellationToken cancellationToken) =>
        users.ListAsync(filter, paging, cancellationToken);

    /// <summary>Admin: one user with their group memberships, current-period allocation and masked key.</summary>
    /// <response code="404">No such user.</response>
    [HttpGet("{userId:int}")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<UserDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<UserDetailResponse> GetAsync(int userId, CancellationToken cancellationToken) =>
        users.GetAsync(userId, cancellationToken);

    /// <summary>
    /// Admin: sets the user-level quota override. A finite quota must be exactly one of the configured
    /// tier caps (<c>GET /quota/tiers</c>) — a monthly budget <em>is</em> a gateway tier. Re-resolves the
    /// current period, which moves the user's APIM subscription onto the new tier product.
    /// </summary>
    /// <response code="400">The quota matches no configured tier; the message lists the allowed values.</response>
    /// <response code="404">No such user.</response>
    /// <response code="502">The gateway refused the tier move; nothing was saved.</response>
    [HttpPut("{userId:int}/quota")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public Task<UserResponse> UpdateQuotaAsync(int userId, [FromBody] UpdateUserQuotaRequest request, CancellationToken cancellationToken) =>
        users.UpdateQuotaAsync(userId, request, cancellationToken);

    /// <summary>
    /// Admin: re-activates a deactivated user and runs the full provision pipeline — quota re-resolved
    /// and a new APIM subscription minted (an orphan of the same name is adopted rather than
    /// duplicated). The new key is not returned here; the developer reveals it with
    /// <c>POST /keys/me/reveal</c>.
    /// </summary>
    /// <response code="404">No such user.</response>
    /// <response code="409">The user is already active.</response>
    /// <response code="502">The gateway refused the subscription; the user stays deactivated.</response>
    /// <response code="503">APIM key management is not configured on this host.</response>
    [HttpPost("{userId:int}/activate")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public Task<UserResponse> ActivateAsync(int userId, CancellationToken cancellationToken) =>
        users.ActivateAsync(userId, cancellationToken);

    /// <summary>
    /// Admin: deactivates a user — deletes their APIM subscription, clears the stored key, hard-stops
    /// this month's allocation and rejects their pending quota-increase requests. To take away only the
    /// key while the user stays active, use <c>DELETE /keys/{userId}</c> instead.
    /// </summary>
    /// <response code="404">No such user.</response>
    /// <response code="409">The user is already deactivated.</response>
    /// <response code="502">The gateway refused the deletion; the user stays active.</response>
    [HttpPost("{userId:int}/deactivate")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public Task<UserResponse> DeactivateAsync(int userId, CancellationToken cancellationToken) =>
        users.DeactivateAsync(userId, cancellationToken);

    /// <summary>
    /// Reconciles the <c>Users</c> table against the people assigned to the FoundryGate application
    /// in Entra (spec &#167;7.2). Idempotent; returns how many users were added, updated and
    /// deactivated. Semantics — no APIM key on import, departed users run the full deprovision
    /// pipeline (#65), never auto-reactivates — are on
    /// <see cref="IEntraUserSyncService.SyncUsersAsync"/>.
    /// </summary>
    /// <response code="200">The run's counts.</response>
    /// <response code="403">The caller has no <c>User</c> row yet (call <c>GET /users/me</c> first) or is not an admin.</response>
    /// <response code="409">The directory returned no assigned users while active users exist locally; nothing was changed.</response>
    /// <response code="503">Entra sync is disabled on this host (<c>Entra:Enabled</c> is false).</response>
    [HttpPost("sync")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<UserSyncResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public Task<UserSyncResult> SyncAsync(CancellationToken cancellationToken) =>
        entraUserSyncService.SyncUsersAsync(cancellationToken);
}
