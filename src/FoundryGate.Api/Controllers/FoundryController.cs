using FoundryGate.Api.Services.Foundry;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Foundry.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/foundry</c> — Azure AI Foundry model deployments (issue #61, plans/20). The
/// deployment endpoints are admin-only; <c>GET /foundry/models</c> is for every authenticated
/// developer. Named <c>FoundryController</c> (not <c>FoundryDeploymentsController</c>) because
/// <see cref="ApiControllerBase"/>'s <c>api/v1/[controller]</c> route derives the path segment from
/// the class name, and the spec's path is <c>/foundry/…</c>.
/// </summary>
/// <remarks>
/// Safety rules are enforced in <see cref="IFoundryDeploymentService"/>, not here: an existing
/// deployment name is a <c>409</c> (never re-PUT), Anthropic-format creation is a <c>400</c>
/// (#107), and delete never recreates. Errors arrive as ProblemDetails via
/// <c>GlobalExceptionHandler</c>.
/// </remarks>
public sealed class FoundryController(IFoundryDeploymentService deploymentService) : ApiControllerBase
{
    /// <summary>Route name for the single-deployment GET, used to build <c>POST</c>'s <c>Location</c> header.</summary>
    public const string GetDeploymentRouteName = "GetFoundryDeployment";

    /// <summary>
    /// Lists every deployment in every configured Foundry account — full admin view (SKU, capacity
    /// in thousands of TPM, provisioning state, timestamps). Pool order, primary account first.
    /// </summary>
    [HttpGet("deployments")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<IReadOnlyList<FoundryDeploymentResponse>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<FoundryDeploymentResponse>> ListDeploymentsAsync(CancellationToken cancellationToken) =>
        deploymentService.ListDeploymentsAsync(cancellationToken);

    /// <summary>
    /// The models the configured accounts can serve — model name, version, deployable SKUs and a
    /// suggested capacity (#173). What the create dialog's model and SKU pickers offer, in place of
    /// the hardcoded list that shipped with it. Served from a 5-minute cache; Anthropic-format models
    /// are listed for visibility even though creating one is refused.
    /// </summary>
    [HttpGet("catalog")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<IReadOnlyList<FoundryCatalogEntryResponse>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<FoundryCatalogEntryResponse>> ListCatalogAsync(CancellationToken cancellationToken) =>
        deploymentService.ListCatalogAsync(cancellationToken);

    /// <summary>One deployment by account and name — poll this after a create for <c>Succeeded</c>.</summary>
    [HttpGet("deployments/{accountName}/{deploymentName}", Name = GetDeploymentRouteName)]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<FoundryDeploymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<FoundryDeploymentResponse> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken) =>
        deploymentService.GetDeploymentAsync(accountName, deploymentName, cancellationToken);

    /// <summary>
    /// Creates one deployment in one configured account. <c>201</c> with the deployment as ARM
    /// reported it on acceptance (usually <c>Accepted</c>/<c>Creating</c>) and a <c>Location</c>
    /// pointing at <see cref="GetDeploymentAsync"/>. <c>409</c> if the name already exists in that
    /// account; <c>400</c> for an unconfigured account or an Anthropic model format (#107).
    /// </summary>
    [HttpPost("deployments")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<FoundryDeploymentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FoundryDeploymentResponse>> CreateDeploymentAsync(
        [FromBody] CreateFoundryDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var created = await deploymentService.CreateDeploymentAsync(request, cancellationToken);

        return CreatedAtRoute(
            GetDeploymentRouteName,
            new { accountName = created.AccountName, deploymentName = created.DeploymentName },
            created);
    }

    /// <summary>
    /// Rebalances one deployment's TPM in place: <c>{ "capacity": 25 }</c> sets ARM's
    /// <c>sku.capacity</c> to 25 (thousands of tokens per minute) and returns the deployment as ARM
    /// reported it on acceptance. <c>404</c> for an unknown account or deployment; <c>400</c> for a
    /// capacity out of range or an Anthropic-format deployment (#130). Asking for the capacity it
    /// already has is a no-op that returns the deployment unchanged.
    /// </summary>
    /// <remarks>
    /// PATCH, not PUT, all the way down: the ARM operation is <c>Deployments_Update</c>, whose body
    /// carries <c>sku</c> and <c>tags</c> only — the model is never re-sent, which is what separates
    /// this from the create path the API refuses to point at an existing deployment.
    /// </remarks>
    [HttpPatch("deployments/{accountName}/{deploymentName}/capacity")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<FoundryDeploymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<FoundryDeploymentResponse> UpdateDeploymentCapacityAsync(
        string accountName,
        string deploymentName,
        [FromBody] UpdateFoundryDeploymentCapacityRequest request,
        CancellationToken cancellationToken) =>
        deploymentService.UpdateCapacityAsync(accountName, deploymentName, request, cancellationToken);

    /// <summary>Deletes one deployment (<c>204</c>). Never recreates; <c>404</c> when absent.</summary>
    [HttpDelete("deployments/{accountName}/{deploymentName}")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        await deploymentService.DeleteDeploymentAsync(accountName, deploymentName, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// The developer view — any authenticated user. Distinct deployment names with model, version,
    /// format and provisioning state; what the <c>/me</c> "Configure your CLI" panel lists.
    /// </summary>
    [HttpGet("models")]
    [ProducesResponseType<IReadOnlyList<FoundryModelResponse>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<FoundryModelResponse>> ListModelsAsync(CancellationToken cancellationToken) =>
        deploymentService.ListModelsAsync(cancellationToken);
}
