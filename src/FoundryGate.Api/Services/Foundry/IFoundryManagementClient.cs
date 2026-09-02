using FoundryGate.Domain.Foundry.Contracts;

namespace FoundryGate.Api.Services.Foundry;

/// <summary>
/// The thin seam between FoundryGate and Azure Resource Manager for Foundry model deployments:
/// four primitive operations against one account, no policy. Everything that <em>decides</em> —
/// which accounts exist, whether a name may be created, whether a format is allowed, what to
/// audit — lives in <see cref="IFoundryDeploymentService"/>; this interface only translates
/// between <see cref="FoundryDeploymentResponse"/> and the SDK. It exists so the service and the
/// controllers are testable without Azure (tests substitute an in-memory fake) and so the ARM SDK
/// types never leak past <c>Services/Foundry</c>.
/// </summary>
/// <remarks>
/// Two kinds of "not found" are kept apart: the <b>account</b> missing is
/// <see cref="FoundryAccountNotFoundException"/> from every method (a configuration/server
/// problem); the <b>deployment</b> missing is <see langword="null"/> from
/// <see cref="GetDeploymentAsync"/> and <see langword="false"/> from
/// <see cref="DeleteDeploymentAsync"/> (a legitimate 404). ARM's <c>409</c> becomes
/// <see cref="Domain.Exceptions.ConflictException"/>; any other ARM failure (403 from an
/// under-privileged identity, 400 from a quota or model-catalog rejection, 5xx) is left to surface
/// as <c>Azure.RequestFailedException</c> — a 500 with the detail in the server log, never on the
/// wire — because it describes the <em>gateway's</em> identity or quota, not the caller's request.
/// </remarks>
public interface IFoundryManagementClient
{
    /// <summary>Every deployment in <paramref name="accountName"/>, in ARM's enumeration order.</summary>
    /// <exception cref="FoundryAccountNotFoundException">The account itself does not exist.</exception>
    Task<IReadOnlyList<FoundryDeploymentResponse>> ListDeploymentsAsync(string accountName, CancellationToken cancellationToken);

    /// <summary>
    /// The models <paramref name="accountName"/> can serve, as ARM's account-model list reports them
    /// (#173) — the same catalogue <c>az cognitiveservices model list</c> shows, scoped to the account
    /// rather than to a region, so it already accounts for the account's kind and the subscription's
    /// entitlements. One entry per model/version, each carrying its deployable SKUs.
    /// </summary>
    /// <exception cref="FoundryAccountNotFoundException">The account itself does not exist.</exception>
    Task<IReadOnlyList<FoundryCatalogEntryResponse>> ListCatalogAsync(string accountName, CancellationToken cancellationToken);

    /// <summary>One deployment, or <see langword="null"/> when no deployment of that name exists in the account.</summary>
    /// <exception cref="FoundryAccountNotFoundException">The account itself does not exist.</exception>
    Task<FoundryDeploymentResponse?> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken);

    /// <summary>
    /// Starts creating a deployment and returns its state as ARM reports it immediately after the
    /// PUT is accepted — typically <c>Accepted</c>/<c>Creating</c>; ARM validates asynchronously
    /// (minutes, for some models), so callers poll <see cref="GetDeploymentAsync"/> for
    /// <c>Succeeded</c>. The caller (<see cref="IFoundryDeploymentService"/>) has already
    /// established the name does not exist: this method is a PUT and must never be pointed at an
    /// existing deployment (CLAUDE.md: never re-PUT).
    /// </summary>
    /// <exception cref="FoundryAccountNotFoundException">The account itself does not exist.</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">ARM refused with 409 (a concurrent create of the same name, or a name racing this call).</exception>
    Task<FoundryDeploymentResponse> CreateDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Changes a live deployment's <c>sku.capacity</c> (thousands of TPM) in place and returns it as
    /// ARM reports it on acceptance — the one mutation that is safe on an existing deployment (#130).
    /// </summary>
    /// <remarks>
    /// <b>PATCH, never PUT.</b> This is ARM's <c>Deployments_Update</c>
    /// (<c>PATCH .../deployments/{name}</c>), whose request body schema is <c>{ sku, tags }</c> and has
    /// no <c>model</c> field — so it structurally cannot re-send the model or the Marketplace
    /// attestation, which is what makes it different from <see cref="CreateDeploymentAsync"/>'s
    /// <c>CreateOrUpdate</c> PUT (CLAUDE.md; E-006/E-007). ARM requires the sku's <c>name</c> alongside
    /// the capacity, so the caller passes the deployment's current
    /// <see cref="FoundryDeploymentResponse.SkuName"/> back unchanged.
    /// </remarks>
    /// <param name="accountName">The Foundry account the deployment lives in.</param>
    /// <param name="deploymentName">The deployment to resize.</param>
    /// <param name="skuName">The deployment's current <c>sku.name</c> — sent unchanged; ARM's patch body requires it.</param>
    /// <param name="capacity">New <c>sku.capacity</c>, in thousands of TPM.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="FoundryAccountNotFoundException">The account itself does not exist.</exception>
    /// <exception cref="KeyNotFoundException">No deployment of that name exists in the account.</exception>
    Task<FoundryDeploymentResponse> UpdateCapacityAsync(string accountName, string deploymentName, string skuName, int capacity, CancellationToken cancellationToken);

    /// <summary>
    /// Starts deleting a deployment. <see langword="true"/> when ARM accepted the delete,
    /// <see langword="false"/> when no such deployment existed — nothing is retried or
    /// recreated either way.
    /// </summary>
    /// <exception cref="FoundryAccountNotFoundException">The account itself does not exist.</exception>
    Task<bool> DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken);
}
