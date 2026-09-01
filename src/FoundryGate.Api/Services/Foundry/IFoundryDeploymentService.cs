using FoundryGate.Domain.Foundry.Contracts;

namespace FoundryGate.Api.Services.Foundry;

/// <summary>
/// Foundry model deployment lifecycle for the gateway's configured accounts (issue #61,
/// plans/20-foundry-provisioning.md; the "Foundry model provision pipeline" in plans/21). Owns
/// every decision the ARM seam (<see cref="IFoundryManagementClient"/>) deliberately doesn't:
/// which accounts are in scope (<c>Gateway:FoundryAccountNames</c> — nothing else is reachable
/// through the API, whatever the identity could touch), the create-once rule, which model formats
/// may be created, and the audit trail.
/// </summary>
/// <remarks>
/// <para><b>Safety rules (CLAUDE.md "Architecture ground truths"; fable-refactor-log.md E-006/E-007):</b></para>
/// <list type="bullet">
/// <item><description><see cref="CreateDeploymentAsync"/> checks for an existing deployment of the same name first and throws <see cref="Domain.Exceptions.ConflictException"/> (409). The SDK call is PUT-shaped (<c>CreateOrUpdate</c>); re-PUTing an existing Anthropic deployment drives it to <c>Failed</c>, so the API never reaches the PUT with an existing name.</description></item>
/// <item><description><see cref="DeleteDeploymentAsync"/> deletes exactly once and never recreates. There is no "update"/"replace" operation: replacing a deployment is an explicit admin delete followed by an explicit admin create, each audited.</description></item>
/// <item><description>Anthropic-format creation is refused (400) until the ARM SDK can carry the Marketplace <c>modelProviderData</c> attestation and the host identity holds the Marketplace permissions — #107. Existing Anthropic deployments (created by infra) list and delete normally.</description></item>
/// </list>
/// <para>
/// Mutations audit <em>after</em> ARM accepts them (<c>foundry.deployment.created</c> /
/// <c>foundry.deployment.deleted</c>, target <c>{accountName}/{deploymentName}</c>) and save in
/// the same call: an ARM failure leaves no audit row, and an audit row always describes a change
/// ARM accepted. External mutation + local row cannot be one transaction; the order chosen never
/// records a change that did not happen.
/// </para>
/// </remarks>
public interface IFoundryDeploymentService
{
    /// <summary>
    /// Every deployment in every configured account (admin view), ordered by account (pool order,
    /// primary first) then deployment name. Accounts are queried concurrently.
    /// </summary>
    Task<IReadOnlyList<FoundryDeploymentResponse>> ListDeploymentsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One deployment by account and name (admin view).
    /// </summary>
    /// <exception cref="KeyNotFoundException">The account is not one of the configured accounts, or no such deployment exists in it.</exception>
    Task<FoundryDeploymentResponse> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken);

    /// <summary>
    /// The developer view: distinct deployment names across all configured accounts, with the
    /// developer-facing fields only. A pooled model (deployed in every region) is listed once,
    /// reported <c>Succeeded</c> if any account serves it (the APIM pool routes around the rest),
    /// otherwise with the primary account's state. Ordered by deployment name.
    /// </summary>
    Task<IReadOnlyList<FoundryModelResponse>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates one deployment in one account and audits <c>foundry.deployment.created</c>. Returns
    /// the deployment as ARM reported it on acceptance (usually <c>Accepted</c>/<c>Creating</c> —
    /// poll <see cref="GetDeploymentAsync"/> for <c>Succeeded</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The account is not configured (400), or the model format cannot be created through the API (Anthropic, 400 — see the type remarks and #107).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">A deployment of that name already exists in the account (409) — the API never re-PUTs an existing deployment.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (403 — call <c>GET /users/me</c> first).</exception>
    Task<FoundryDeploymentResponse> CreateDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one deployment and audits <c>foundry.deployment.deleted</c>. In-flight gateway
    /// requests pinned to this deployment name start failing with the backend's 404 as soon as ARM
    /// finishes — retarget the alias map (plans/25) first when rotating models.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The account is not one of the configured accounts, or no such deployment exists in it (404).</exception>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (403 — call <c>GET /users/me</c> first).</exception>
    Task DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken);
}
