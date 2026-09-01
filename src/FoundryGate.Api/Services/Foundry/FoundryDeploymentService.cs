using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Data;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Foundry.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace FoundryGate.Api.Services.Foundry;

/// <summary>
/// Default <see cref="IFoundryDeploymentService"/>. Scoped: it shares the request's
/// <see cref="AppDbContext"/> with <see cref="IAuditService"/> so the audit row commits in this
/// service's own <c>SaveChangesAsync</c>. See the interface remarks for the ownership split
/// (infra owns Claude, the API owns OpenAI), the commit-point rule and the 503 semantics.
/// </summary>
public sealed class FoundryDeploymentService(
    IFoundryManagementClient managementClient,
    AppSettings appSettings,
    IAuditService auditService,
    ICurrentUserAccessor currentUser,
    AppDbContext dbContext,
    IMemoryCache cache,
    ILogger<FoundryDeploymentService> logger)
    : IFoundryDeploymentService
{
    /// <summary><see cref="IMemoryCache"/> key for the developer view.</summary>
    public const string ModelsCacheKey = "FoundryGate.Foundry.Models";

    /// <summary>How long <see cref="ListModelsAsync"/> serves a cached answer; creates/deletes invalidate early.</summary>
    public static readonly TimeSpan ModelsCacheDuration = TimeSpan.FromSeconds(30);

    private const string SucceededState = "Succeeded";
    private static readonly string AnthropicFormat = FoundryModelFormatType.Anthropic.ToString();

    /// <inheritdoc />
    public async Task<IReadOnlyList<FoundryDeploymentResponse>> ListDeploymentsAsync(CancellationToken cancellationToken)
    {
        var accountNames = ConfiguredAccountNames();

        // One ARM list per account, concurrently — the accounts are independent resources. Ordering is
        // restored afterwards: account in pool order (primary first), then deployment name.
        IReadOnlyList<FoundryDeploymentResponse>[] perAccount;
        try
        {
            perAccount = await Task.WhenAll(
                accountNames.Select(account => managementClient.ListDeploymentsAsync(account, cancellationToken)));
        }
        catch (FoundryAccountNotFoundException ex)
        {
            throw MissingAccount(ex);
        }

        return Order(perAccount.SelectMany(deployments => deployments), accountNames);
    }

    /// <inheritdoc />
    public async Task<FoundryDeploymentResponse> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var account = TryResolveConfiguredAccount(accountName)
            ?? throw new KeyNotFoundException($"'{accountName}' is not one of this gateway's Foundry accounts.");

        return await GetExistingAsync(account, deploymentName, cancellationToken)
            ?? throw DeploymentNotFound(account, deploymentName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FoundryModelResponse>> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(ModelsCacheKey, out IReadOnlyList<FoundryModelResponse>? cached) && cached is not null)
        {
            return cached;
        }

        var accountNames = ConfiguredAccountNames();

        // Concurrent like the admin list, but a missing account is skipped (null) rather than fatal:
        // a decommissioned region must not blank every developer's CLI panel.
        var perAccount = await Task.WhenAll(accountNames.Select(account => TryListAsync(account, cancellationToken)));

        // Order() puts the primary account first, so "first in group" below is the primary's row
        // whenever no account reports Succeeded.
        var models = Order(perAccount.Where(d => d is not null).SelectMany(d => d!), accountNames)
            .GroupBy(d => d.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
                group.FirstOrDefault(d => string.Equals(d.ProvisioningState, SucceededState, StringComparison.OrdinalIgnoreCase))
                ?? group.First())
            .OrderBy(d => d.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .Select(d => new FoundryModelResponse(d.DeploymentName, d.ModelName, d.ModelVersion, d.ModelFormat, d.ProvisioningState))
            .ToList();

        _ = cache.Set<IReadOnlyList<FoundryModelResponse>>(ModelsCacheKey, models, ModelsCacheDuration);
        return models;
    }

    /// <inheritdoc />
    public async Task<FoundryDeploymentResponse> CreateDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 403 for an unprovisioned caller before any ARM call — see the interface remarks.
        _ = await currentUser.GetRequiredUserAsync(cancellationToken);

        var accountNames = ConfiguredAccountNames();
        var account = TryResolveConfiguredAccount(request.AccountName)
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
                "Anthropic (Claude) deployments cannot be created through the API: creation requires the Marketplace " +
                "modelProviderData attestation, which the current Azure SDK cannot send and the API's identity is not " +
                "permitted to make (see GitHub issues #107 and #126). Claude deployments are managed by the infrastructure " +
                "deploy (infra/main.bicep, createModelDeployments=true, first run only). OpenAI-format deployments are supported.");
        }

        // Create-once: an existing name is a 409, never a re-PUT (CLAUDE.md; E-006/E-007).
        var existing = await GetExistingAsync(account, request.DeploymentName, cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException(
                $"Deployment '{request.DeploymentName}' already exists in Foundry account '{account}' " +
                $"(model {existing.ModelName} {existing.ModelVersion}, state {existing.ProvisioningState}). " +
                "Deployments are never re-created in place: delete it first, or choose another name.");
        }

        FoundryDeploymentResponse created;
        try
        {
            created = await managementClient.CreateDeploymentAsync(request with { AccountName = account }, cancellationToken);
        }
        catch (FoundryAccountNotFoundException ex)
        {
            throw MissingAccount(ex);
        }

        // ---- commit point: ARM has accepted the create. Nothing below observes cancellationToken. ----
        cache.Remove(ModelsCacheKey);

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

        await AuditAfterCommitAsync(
            AuditActions.FoundryDeploymentCreated,
            created.AccountName,
            created.DeploymentName,
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
            });

        return created;
    }

    /// <inheritdoc />
    public async Task DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        // 403 for an unprovisioned caller before any ARM call — see the interface remarks.
        _ = await currentUser.GetRequiredUserAsync(cancellationToken);

        var account = TryResolveConfiguredAccount(accountName)
            ?? throw new KeyNotFoundException($"'{accountName}' is not one of this gateway's Foundry accounts.");

        // Read first: the format guard and the audit row need the deployment's identity, and a missing
        // deployment is a clean 404 rather than an ARM error shape.
        var existing = await GetExistingAsync(account, deploymentName, cancellationToken)
            ?? throw DeploymentNotFound(account, deploymentName);

        if (string.Equals(existing.ModelFormat, AnthropicFormat, StringComparison.OrdinalIgnoreCase))
        {
            // Symmetric with the create refusal: the API cannot recreate a Claude deployment (#126), and
            // infra can only recreate ALL of an account's deployments — re-PUTing the survivors (E-006).
            // Deleting one would be a one-way door whose recovery damages its neighbours (E-007).
            throw new ArgumentException(
                $"Deployment '{existing.DeploymentName}' is an Anthropic (Claude) deployment, which the API does not delete: " +
                "Claude deployments are created once by the infrastructure deploy and cannot be recreated through the API " +
                "(see GitHub issue #126). Remove it from infra/main.bicep's model deployments and delete it deliberately in " +
                "Azure if it must go. OpenAI-format deployments can be deleted here.");
        }

        bool deleted;
        try
        {
            deleted = await managementClient.DeleteDeploymentAsync(account, existing.DeploymentName, cancellationToken);
        }
        catch (FoundryAccountNotFoundException ex)
        {
            throw MissingAccount(ex);
        }

        if (!deleted)
        {
            // Raced with another delete between the read and the delete: same outcome for the caller.
            throw DeploymentNotFound(account, deploymentName);
        }

        // ---- commit point: ARM has accepted the delete. Nothing below observes cancellationToken. ----
        cache.Remove(ModelsCacheKey);

        logger.LogInformation(
            "Foundry deployment {Account}/{Deployment} delete accepted ({Format} {Model} {Version})",
            account,
            existing.DeploymentName,
            existing.ModelFormat,
            existing.ModelName,
            existing.ModelVersion);

        await AuditAfterCommitAsync(
            AuditActions.FoundryDeploymentDeleted,
            account,
            existing.DeploymentName,
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
            });
    }

    /// <summary>
    /// Audit + save for a change ARM has already accepted. Runs on <see cref="CancellationToken.None"/>:
    /// the caller's disconnect must not turn an accepted change into an unaudited one. If the save
    /// itself fails, the orphan is logged at Error with its full identity for manual reconciliation
    /// (a retried create meets a 409) and the exception propagates.
    /// </summary>
    private async Task AuditAfterCommitAsync(string action, string accountName, string deploymentName, object details)
    {
        try
        {
            _ = await auditService.LogAsync(action, AuditTargetTypes.FoundryDeployment, $"{accountName}/{deploymentName}", details, CancellationToken.None);
            _ = await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Foundry deployment {Account}/{Deployment}: Azure accepted the change ({Action}) but the audit row could not be saved — reconcile manually",
                accountName,
                deploymentName,
                action);
            throw;
        }
    }

    /// <summary>Reads one deployment, mapping a missing <em>account</em> to 503 (a missing deployment is <see langword="null"/>).</summary>
    private async Task<FoundryDeploymentResponse?> GetExistingAsync(string account, string deploymentName, CancellationToken cancellationToken)
    {
        try
        {
            return await managementClient.GetDeploymentAsync(account, deploymentName, cancellationToken);
        }
        catch (FoundryAccountNotFoundException ex)
        {
            throw MissingAccount(ex);
        }
    }

    /// <summary>Lists one account for the developer view; <see langword="null"/> (and a Warning) when the account is missing.</summary>
    private async Task<IReadOnlyList<FoundryDeploymentResponse>?> TryListAsync(string account, CancellationToken cancellationToken)
    {
        try
        {
            return await managementClient.ListDeploymentsAsync(account, cancellationToken);
        }
        catch (FoundryAccountNotFoundException ex)
        {
            logger.LogWarning(
                ex,
                "Foundry account {Account} is configured in Gateway:FoundryAccountNames but was not found; skipping it in the developer model list",
                account);
            return null;
        }
    }

    private static List<FoundryDeploymentResponse> Order(IEnumerable<FoundryDeploymentResponse> deployments, List<string> accountNames) =>
        deployments
            .OrderBy(d => accountNames.FindIndex(a => string.Equals(a, d.AccountName, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(d => d.DeploymentName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static KeyNotFoundException DeploymentNotFound(string account, string deploymentName) =>
        new($"Deployment '{deploymentName}' was not found in Foundry account '{account}'.");

    /// <summary>A configured account that Azure does not have: the operator's problem (503), named without the resource group.</summary>
    private static FeatureNotConfiguredException MissingAccount(FoundryAccountNotFoundException ex) =>
        new(
            $"Foundry account '{ex.AccountName}' is listed in Gateway:FoundryAccountNames but was not found in Azure " +
            "(or the API's identity cannot see it). Fix the configuration or the account before managing deployments.",
            ex);

    private List<string> ConfiguredAccountNames()
    {
        var gateway = appSettings.Gateway;
        if (!gateway.IsFoundryConfigured)
        {
            throw new FeatureNotConfiguredException(
                "Foundry deployment management is not configured: set Gateway:SubscriptionId, Gateway:ResourceGroup and " +
                "Gateway:FoundryAccountNames (infra sets these on the Container App as Gateway__*; see issue #108).");
        }

        return gateway.FoundryAccountNames;
    }

    /// <summary>Resolves a route/body account name against the configured list (case-insensitively), returning the configured spelling or <see langword="null"/>.</summary>
    private string? TryResolveConfiguredAccount(string accountName) =>
        ConfiguredAccountNames().FirstOrDefault(a => string.Equals(a, accountName, StringComparison.OrdinalIgnoreCase));
}
