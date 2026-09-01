using FoundryGate.Domain.Foundry.Contracts;

namespace FoundryGate.Api.Services.Foundry;

/// <summary>
/// Foundry model deployment lifecycle for the gateway's configured accounts (issue #61,
/// plans/20-foundry-provisioning.md; the "Foundry model provision pipeline" in plans/21). Owns
/// every decision the ARM seam (<see cref="IFoundryManagementClient"/>) deliberately doesn't:
/// which accounts are in scope (<c>Gateway:FoundryAccountNames</c> — nothing else is reachable
/// through the API, whatever the identity could touch), the create-once rule, which model formats
/// the API may create or delete, and the audit trail.
/// </summary>
/// <remarks>
/// <para><b>Ownership split.</b> Claude (Anthropic-format) deployments belong to the infrastructure
/// deploy end to end: <c>infra/main.bicep</c> creates them once (<c>createModelDeployments</c>),
/// and the API neither creates nor deletes them — it lists them. The API manages OpenAI-format
/// deployments. Reason (CLAUDE.md "Architecture ground truths"; fable-refactor-log.md E-006/E-007):
/// Anthropic deployments need a Marketplace attestation the SDK cannot send (#126/#107), a re-PUT
/// drives one to <c>Failed</c>, delete/recreate churn has wedged a whole subscription, and Bicep
/// can only recreate <em>all</em> deployments of an account — so an API delete of one Claude
/// deployment would be a one-way door whose only recovery damages its neighbours.</para>
/// <para><b>Safety rules:</b></para>
/// <list type="bullet">
/// <item><description><see cref="CreateDeploymentAsync"/> checks for an existing deployment of the same name first and throws <see cref="Domain.Exceptions.ConflictException"/> (409). The SDK call is PUT-shaped (<c>CreateOrUpdate</c>); the API never reaches the PUT with an existing name.</description></item>
/// <item><description><see cref="CreateDeploymentAsync"/> and <see cref="DeleteDeploymentAsync"/> refuse Anthropic-format deployments (400 → #126).</description></item>
/// <item><description><see cref="DeleteDeploymentAsync"/> deletes exactly once and never recreates. There is no update/replace operation (capacity resize is #130).</description></item>
/// </list>
/// <para><b>Audit and the commit point.</b> Mutations resolve the caller's <c>User</c> row before
/// touching ARM (an unprovisioned admin is refused with nothing changed), then audit
/// <c>foundry.deployment.created</c> / <c>foundry.deployment.deleted</c> (target
/// <c>{accountName}/{deploymentName}</c>) <em>after</em> ARM accepts, saving in the same call.
/// Once ARM has accepted, the audit row and save deliberately ignore the request's cancellation
/// token: a client that disconnects mid-create must not turn an accepted deployment into an
/// unaudited one. What remains is the irreducible window where the database save itself fails
/// after ARM accepted — the deployment exists (or is gone) with no audit row; it is logged at
/// Error with the full identity so it can be reconciled by hand, and a retried create meets a
/// 409 rather than a duplicate.</para>
/// <para><b>Configuration and missing accounts.</b> An absent <c>Gateway</c> section, or a
/// configured account that does not exist in Azure, is
/// <see cref="Domain.Exceptions.FeatureNotConfiguredException"/> (503) on the admin paths — an
/// operator problem, described on the wire without the resource-group name. The developer view
/// (<see cref="ListModelsAsync"/>) instead skips a missing account with a Warning and serves the
/// rest: one decommissioned region must not blank every developer's CLI panel.</para>
/// </remarks>
public interface IFoundryDeploymentService
{
    /// <summary>
    /// Every deployment in every configured account (admin view), ordered by account (pool order,
    /// primary first) then deployment name. Accounts are queried concurrently and always live — no
    /// cache, so the admin grid's provisioning-state chips are current on refresh.
    /// </summary>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">Foundry management is not configured, or a configured account does not exist (503).</exception>
    Task<IReadOnlyList<FoundryDeploymentResponse>> ListDeploymentsAsync(CancellationToken cancellationToken);

    /// <summary>One deployment by account and name (admin view).</summary>
    /// <exception cref="KeyNotFoundException">The account is not one of the configured accounts, or no such deployment exists in it (404).</exception>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">Foundry management is not configured, or the configured account does not exist in Azure (503).</exception>
    Task<FoundryDeploymentResponse> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken);

    /// <summary>
    /// The developer view: distinct deployment names across all configured accounts, with the
    /// developer-facing fields only. A pooled model (deployed in every region) is listed once,
    /// reported <c>Succeeded</c> if any account serves it (the APIM pool routes around the rest),
    /// otherwise with the primary account's state. Ordered by deployment name. Served from a short
    /// in-memory cache (<c>FoundryDeploymentService.ModelsCacheDuration</c>, 30 s; invalidated by
    /// every create/delete through this service) because every developer's <c>/me</c> page calls it
    /// and inventory changes weekly. Missing accounts are skipped with a Warning.
    /// </summary>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">Foundry management is not configured at all (503).</exception>
    Task<IReadOnlyList<FoundryModelResponse>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates one OpenAI-format deployment in one account and audits <c>foundry.deployment.created</c>.
    /// Returns the deployment as ARM reported it on acceptance (usually <c>Accepted</c>/<c>Creating</c> —
    /// poll <see cref="GetDeploymentAsync"/> for <c>Succeeded</c>).
    /// </summary>
    /// <exception cref="ArgumentException">The account is not configured (400), or the model format is Anthropic (400 — see the type remarks, #126).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">A deployment of that name already exists in the account (409) — the API never re-PUTs an existing deployment.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (403 — call <c>GET /users/me</c> first).</exception>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">Foundry management is not configured, or the configured account does not exist in Azure (503).</exception>
    Task<FoundryDeploymentResponse> CreateDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one OpenAI-format deployment and audits <c>foundry.deployment.deleted</c>. In-flight
    /// gateway requests pinned to this deployment name start failing with the backend's 404 as soon
    /// as ARM finishes — retarget the alias map (plans/25) first when rotating models.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The account is not one of the configured accounts, or no such deployment exists in it (404).</exception>
    /// <exception cref="ArgumentException">The deployment is Anthropic-format (400 — Claude deployments are managed by infra; see the type remarks, #126).</exception>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (403 — call <c>GET /users/me</c> first).</exception>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">Foundry management is not configured, or the configured account does not exist in Azure (503).</exception>
    Task DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken);
}
