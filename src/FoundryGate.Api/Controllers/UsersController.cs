using FoundryGate.Api.Services.Entra;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/users</c> (spec &#167;4.1). This wave (#40) adds only the admin-triggered Entra bulk
/// sync; the remaining actions arrive with their own issues — <c>GET /users/me</c> with first-login
/// auto-provisioning (#28) and the admin list/detail/quota/activate/deactivate surface (#29) — so
/// admin-only authorization is declared per action rather than on the class, leaving room for the
/// developer-facing <c>/me</c>.
/// </summary>
public sealed class UsersController(IEntraUserSyncService entraUserSyncService) : ApiControllerBase
{
    /// <summary>
    /// Reconciles the <c>Users</c> table against the people assigned to the FoundryGate application
    /// in Entra (spec &#167;7.2). Idempotent; returns how many users were added, updated and
    /// deactivated. Semantics — no APIM key on import, departed users flagged inactive (full
    /// deprovision arrives with #65), never auto-reactivates — are on
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
