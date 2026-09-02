using FoundryGate.Api.Services.Config;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/config</c> (spec &#167;4.6; issue #161) — the fork-wide <c>SystemConfiguration</c>
/// editor behind the admin <c>/config</c> page (#55). Admin-only for the whole controller: these
/// values steer quota resolution and the gateway addressing.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
public sealed class ConfigController(IConfigService configService) : ApiControllerBase
{
    /// <summary>Every configuration key with its value, when it last changed, and which admin changed it.</summary>
    /// <response code="200">The rows, ordered by key.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SystemConfigEntryResponse>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<SystemConfigEntryResponse>> ListAsync(CancellationToken cancellationToken) =>
        configService.ListAsync(cancellationToken);

    /// <summary>
    /// Sets one key's value. Validated per key (<see cref="SystemConfigValidator"/>), stamped with the
    /// calling admin and the current time, and audited (<c>config.updated</c>) in the same save.
    /// </summary>
    /// <response code="200">The row as it now stands.</response>
    /// <response code="400">The value breaks the key's rule; the <c>detail</c> states the rule.</response>
    /// <response code="403">The caller is not an admin, or has no <c>User</c> row yet (call <c>GET /users/me</c> first).</response>
    /// <response code="404">No such configuration key — including one retired by #164/#123, whose row no longer exists.</response>
    /// <response code="409">
    /// The optional <c>expectedUpdatedDate</c> does not match the stored row: another admin wrote the key
    /// first (#170). The <c>detail</c> carries the current value, when it changed and who changed it.
    /// </response>
    [HttpPut("{key}")]
    [ProducesResponseType<SystemConfigEntryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<SystemConfigEntryResponse> UpdateAsync(
        string key,
        [FromBody] UpdateSystemConfigRequest request,
        CancellationToken cancellationToken) =>
        configService.UpdateAsync(key, request, cancellationToken);
}
