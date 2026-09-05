using FoundryGate.Api.Services.Gateway;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Gateway.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/gateway</c> — the gateway's own configuration surface (#225). Today that is the
/// per-tier model allowlist: the alias map each quota-tier product carries as the APIM named value
/// <c>fg-model-map-{tier}</c> (#86, plans/25). The map <b>is</b> the allowlist — a model a tier does
/// not list is a <c>403 model_not_permitted</c> at the gateway — so these endpoints are what the
/// <c>/models</c> admin page uses to grant, retarget and remove models without an infra deploy.
/// </summary>
/// <remarks>
/// Admin-only at class level: this changes what every developer on a tier can reach. Errors arrive as
/// ProblemDetails via <c>GlobalExceptionHandler</c> — <c>404</c> for an unknown tier, <c>400</c> for a
/// map the gateway would answer with a 404 instead of an honest 403, <c>409</c> when APIM refuses the
/// write, <c>503</c> when APIM or Foundry addressing is absent.
/// </remarks>
[Authorize(Policy = PolicyNames.AdminOnly)]
public sealed class GatewayController(IGatewayModelService gatewayModels) : ApiControllerBase
{
    /// <summary>The gateway's quota tiers with the size of each one's current model allowlist.</summary>
    [HttpGet("tiers")]
    [ProducesResponseType<IReadOnlyList<GatewayTierResponse>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<GatewayTierResponse>> ListTiersAsync(CancellationToken cancellationToken) =>
        gatewayModels.ListTiersAsync(cancellationToken);

    /// <summary>
    /// One tier's allowlist as APIM holds it, each row flagged for whether its deployment exists in
    /// the configured Foundry accounts (and which accounts are missing it).
    /// </summary>
    [HttpGet("tiers/{tier}/models")]
    [ProducesResponseType<GatewayTierModelsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<GatewayTierModelsResponse> GetTierModelsAsync(string tier, CancellationToken cancellationToken) =>
        gatewayModels.GetTierModelsAsync(tier, cancellationToken);

    /// <summary>
    /// Replaces a tier's allowlist in full (<c>{ "aliases": [ … ] }</c>) and returns it as stored.
    /// A full replace, not a patch: the named value is one JSON document, and "these are the models
    /// this tier may use" is the sentence an admin is writing. An empty list permits nothing.
    /// </summary>
    [HttpPut("tiers/{tier}/models")]
    [ProducesResponseType<GatewayTierModelsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<GatewayTierModelsResponse> ReplaceTierModelsAsync(
        string tier,
        [FromBody] ReplaceTierModelsRequest request,
        CancellationToken cancellationToken) =>
        gatewayModels.ReplaceTierModelsAsync(tier, request, cancellationToken);
}
