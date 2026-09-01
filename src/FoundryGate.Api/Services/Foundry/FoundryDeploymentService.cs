using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Data;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Foundry.Contracts;

namespace FoundryGate.Api.Services.Foundry;

/// <summary>
/// Default <see cref="IFoundryDeploymentService"/>. Scoped: it shares the request's
/// <see cref="AppDbContext"/> with <see cref="IAuditService"/> so the audit row commits in this
/// service's own <c>SaveChangesAsync</c>.
/// </summary>
/// <remarks>
/// Mutations resolve the caller's <c>User</c> row <em>before</em> touching ARM. <see cref="IAuditService.LogAsync"/>
/// would do so anyway, but only after the deployment had already been created or deleted — an
/// unprovisioned admin would then get a 403 for a change Azure had accepted, with no audit row.
/// Checking first keeps "403, call <c>GET /users/me</c>" a no-op on the Azure side.
/// </remarks>
public sealed class FoundryDeploymentService(
    IFoundryManagementClient managementClient,
    AppSettings appSettings,
    IAuditService auditService,
    ICurrentUserAccessor currentUser,
    AppDbContext dbContext,
    ILogger<FoundryDeploymentService> logger)
    : IFoundryDeploymentService
{
    private const string SucceededState = "Succeeded";

    /// <inheritdoc />
    public async Task<IReadOnlyList<FoundryDeploymentResponse>> ListDeploymentsAsync(CancellationToken cancellationToken)
    {
        var accountNames = ConfiguredAccountNames();

        // One ARM list per account, concurrently — the accounts are independent resources. Ordering is
        // restored afterwards: account in pool order (primary first), then deployment name.
        var perAccount = await Task.WhenAll(
            accountNames.Select(account => managementClient.ListDeploymentsAsync(account, cancellationToken)));

        return perAccount
            .SelectMany(deployments => deployments)
            .OrderBy(d => accountNames.IndexOf(d.AccountName))
            .ThenBy(d => d.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<FoundryDeploymentResponse> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var account = RequireConfiguredAccount(accountName);

        return await managementClient.GetDeploymentAsync(account, deploymentName, cancellationToken)
            ?? throw new KeyNotFoundException($"Deployment '{deploymentName}' was not found in Foundry account '{account}'.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FoundryModelResponse>> ListModelsAsync(CancellationToken cancellationToken)
    {
        // ListDeploymentsAsync already yields primary-account rows first, so "first in group" below is
        // the primary account's row whenever no account reports Succeeded.
        var deployments = await ListDeploymentsAsync(cancellationToken);

        return deployments
            .GroupBy(d => d.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
                group.FirstOrDefault(d => string.Equals(d.ProvisioningState, SucceededState, StringComparison.OrdinalIgnoreCase))
                ?? group.First())
            .OrderBy(d => d.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .Select(d => new FoundryModelResponse(d.DeploymentName, d.ModelName, d.ModelVersion, d.ModelFormat, d.ProvisioningState))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<FoundryDeploymentResponse> CreateDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 403 for an unprovisioned caller before any ARM call — see the class remarks.
        _ = await currentUser.GetRequiredUserAsync(cancellationToken);

        var accountNames = ConfiguredAccountNames();
        var account = accountNames.FirstOrDefault(a => string.Equals(a, request.AccountName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"'{request.AccountName}' is not one of this gateway's Foundry accounts ({string.Join(", ", accountNames)}). " +
                "Deployments can only be created in the accounts the gateway routes to (Gateway:FoundryAccountNames).");

        if (request.ModelFormat == FoundryModelFormatType.Anthropic)
        {
            // Two independent blockers, tracked in #107 (permissions) and #126 (SDK): (1) Azure.ResourceManager.CognitiveServices
            // (1.5.2 / api-version 2025-06-01) cannot carry the Marketplace `modelProviderData`
            // attestation Anthropic deployments require, so the PUT would be rejected (E-005); (2) the
            // API's managed identity holds Cognitive Services Contributor only, not the Marketplace/SaaS
            // permissions the attestation needs. Refusing here — before any ARM call — also honours
            // E-007: a failed Anthropic create can wedge the subscription's Marketplace agreement.
            throw new ArgumentException(
                "Anthropic (Claude) deployments cannot be created through the API yet: creation requires the Marketplace " +
                "modelProviderData attestation, which the current Azure SDK cannot send and the API's identity is not " +
                "permitted to make (see GitHub issues #107 and #126). Create Claude deployments through infra/main.bicep " +
                "(createModelDeployments=true, first run only). OpenAI-format deployments are supported.");
        }

        // Create-once: an existing name is a 409, never a re-PUT (CLAUDE.md; E-006/E-007).
        var existing = await managementClient.GetDeploymentAsync(account, request.DeploymentName, cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException(
                $"Deployment '{request.DeploymentName}' already exists in Foundry account '{account}' " +
                $"(model {existing.ModelName} {existing.ModelVersion}, state {existing.ProvisioningState}). " +
                "Deployments are never re-created in place: delete it first, or choose another name.");
        }

        var created = await managementClient.CreateDeploymentAsync(request with { AccountName = account }, cancellationToken);

        logger.LogInformation(
            "Foundry deployment {Account}/{Deployment} create accepted ({Format} {Model} {Version}, {Sku} x{Capacity}); state {State}",
            created.AccountName,
            created.DeploymentName,
            created.ModelFormat,
            created.ModelName,
            created.ModelVersion,
            created.SkuName,
            created.Capacity,
            created.ProvisioningState);

        _ = await auditService.LogAsync(
            AuditActions.FoundryDeploymentCreated,
            AuditTargetTypes.FoundryDeployment,
            TargetId(created.AccountName, created.DeploymentName),
            new
            {
                created.AccountName,
                created.DeploymentName,
                created.ModelFormat,
                created.ModelName,
                created.ModelVersion,
                created.SkuName,
                created.Capacity,
                created.ProvisioningState,
            },
            cancellationToken);
        _ = await dbContext.SaveChangesAsync(cancellationToken);

        return created;
    }

    /// <inheritdoc />
    public async Task DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        // 403 for an unprovisioned caller before any ARM call — see the class remarks.
        _ = await currentUser.GetRequiredUserAsync(cancellationToken);

        var account = RequireConfiguredAccount(accountName);

        // Read first so the audit row can record what was deleted (model/version/state), and so a
        // missing deployment is a clean 404 rather than an ARM error shape.
        var existing = await managementClient.GetDeploymentAsync(account, deploymentName, cancellationToken)
            ?? throw new KeyNotFoundException($"Deployment '{deploymentName}' was not found in Foundry account '{account}'.");

        var deleted = await managementClient.DeleteDeploymentAsync(account, existing.DeploymentName, cancellationToken);
        if (!deleted)
        {
            // Raced with another delete between the read and the delete: same outcome for the caller.
            throw new KeyNotFoundException($"Deployment '{deploymentName}' was not found in Foundry account '{account}'.");
        }

        logger.LogInformation(
            "Foundry deployment {Account}/{Deployment} delete accepted ({Format} {Model} {Version})",
            account,
            existing.DeploymentName,
            existing.ModelFormat,
            existing.ModelName,
            existing.ModelVersion);

        _ = await auditService.LogAsync(
            AuditActions.FoundryDeploymentDeleted,
            AuditTargetTypes.FoundryDeployment,
            TargetId(account, existing.DeploymentName),
            new
            {
                AccountName = account,
                existing.DeploymentName,
                existing.ModelFormat,
                existing.ModelName,
                existing.ModelVersion,
                existing.SkuName,
                existing.Capacity,
                PreviousProvisioningState = existing.ProvisioningState,
            },
            cancellationToken);
        _ = await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string TargetId(string accountName, string deploymentName) => $"{accountName}/{deploymentName}";

    private List<string> ConfiguredAccountNames()
    {
        var gateway = appSettings.Gateway;
        if (!gateway.IsFoundryConfigured)
        {
            // A server-side misconfiguration, not a caller error: surfaces as a 500 with this message in
            // the log (GlobalExceptionHandler never echoes an unmapped exception's message on the wire).
            throw new InvalidOperationException(
                "Foundry deployment management is not configured: set Gateway:SubscriptionId, Gateway:ResourceGroup and " +
                "Gateway:FoundryAccountNames (infra sets these on the Container App as Gateway__*; see issue #108).");
        }

        return gateway.FoundryAccountNames;
    }

    /// <summary>Resolves a route/body account name against the configured list (case-insensitively), returning the configured spelling.</summary>
    private string RequireConfiguredAccount(string accountName) =>
        ConfiguredAccountNames().FirstOrDefault(a => string.Equals(a, accountName, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"'{accountName}' is not one of this gateway's Foundry accounts.");
}
