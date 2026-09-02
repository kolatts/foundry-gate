using System.Net;
using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Models;
using FoundryGate.Api.Configuration;
using FoundryGate.Core.Configuration;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Foundry.Contracts;

namespace FoundryGate.Api.Services.Foundry;

/// <summary>
/// <see cref="IFoundryManagementClient"/> over <c>Azure.ResourceManager.CognitiveServices</c>
/// (1.5.2, api-version 2025-06-01), authenticated as the host's identity (the Container App's
/// user-assigned managed identity holding <c>Cognitive Services Contributor</c> on each Foundry
/// account; Azure CLI locally). Addresses accounts through
/// <see cref="GatewayOptions.SubscriptionId"/> / <see cref="GatewayOptions.ResourceGroup"/>.
/// </summary>
/// <remarks>
/// <para>
/// Both mutations use <see cref="WaitUntil.Started"/>: an OpenAI create usually reaches
/// <c>Succeeded</c> within seconds, but ARM's deployment validation is asynchronous in general
/// (E-007 saw multi-minute <c>Creating</c> phases), and an HTTP request must not block on it.
/// The returned state is whatever ARM reported on the initial response; the UI polls.
/// </para>
/// <para>
/// <b>Account-missing vs deployment-missing.</b> A 404 while <em>listing</em> the deployments
/// collection can only mean the account is gone. A 404 on a single deployment carries ARM's
/// <c>ParentResourceNotFound</c> error code when the <em>account</em> is missing and a plain
/// resource-not-found code when the <em>deployment</em> is; only the latter is reported as
/// absent. (Live confirmation of the code is on #125's checklist.)
/// </para>
/// <para>
/// This SDK version has no <c>modelProviderData</c> on
/// <see cref="CognitiveServicesAccountDeploymentProperties"/> (nor does 1.6.0-beta.4), and its
/// api-version predates the one that accepts it (E-005: only ≥ 2026-xx; the repo's Bicep pins
/// 2026-07-01). An Anthropic-format PUT through this client would therefore reach ARM without the
/// Marketplace attestation and be rejected — which is why <see cref="FoundryDeploymentService"/>
/// refuses Anthropic before calling here. See #107 and #126.
/// </para>
/// </remarks>
public sealed class ArmFoundryManagementClient(ArmClient armClient, AppSettings appSettings) : IFoundryManagementClient
{
    private const string ParentResourceNotFoundCode = "ParentResourceNotFound";

    /// <inheritdoc />
    public async Task<IReadOnlyList<FoundryDeploymentResponse>> ListDeploymentsAsync(string accountName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        var deployments = new List<FoundryDeploymentResponse>();
        try
        {
            await foreach (var deployment in Deployments(accountName).GetAllAsync(cancellationToken).ConfigureAwait(false))
            {
                deployments.Add(Map(accountName, deployment.Data));
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            // The collection belongs to the account; a 404 here is the account, not a deployment.
            throw new FoundryAccountNotFoundException(accountName, ex);
        }

        return deployments;
    }

    /// <inheritdoc />
    public async Task<FoundryDeploymentResponse?> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        try
        {
            var response = await Deployments(accountName).GetAsync(deploymentName, cancellationToken).ConfigureAwait(false);
            return Map(accountName, response.Value.Data);
        }
        catch (RequestFailedException ex) when (IsAccountMissing(ex))
        {
            throw new FoundryAccountNotFoundException(accountName, ex);
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<FoundryDeploymentResponse> CreateDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var data = new CognitiveServicesAccountDeploymentData
        {
            Sku = new CognitiveServicesSku(request.SkuName) { Capacity = request.Capacity },
            Properties = new CognitiveServicesAccountDeploymentProperties
            {
                Model = new CognitiveServicesAccountDeploymentModel
                {
                    Format = request.ModelFormat.ToString(),
                    Name = request.ModelName,
                    Version = request.ModelVersion,
                },
            },
        };

        try
        {
            var operation = await Deployments(request.AccountName)
                .CreateOrUpdateAsync(WaitUntil.Started, request.DeploymentName, data, cancellationToken)
                .ConfigureAwait(false);

            // HasValue would be true only if the long-running operation had completed with a result.
            // Under WaitUntil.Started it is false even for a synchronous 200 (observed on the wire in
            // the #211 review), so in practice the read-back below is always the path taken; the branch
            // stays because it is what the SDK contract promises and costs nothing.
            if (operation.HasValue)
            {
                return Map(request.AccountName, operation.Value.Data);
            }
        }
        catch (RequestFailedException ex) when (IsAccountMissing(ex))
        {
            throw new FoundryAccountNotFoundException(request.AccountName, ex);
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Conflict)
        {
            throw new ConflictException(
                $"Azure refused to create deployment '{request.DeploymentName}' in account '{request.AccountName}' (409): {ex.ErrorCode}. " +
                "Deployment writes are serialized per account — retry once any in-flight create on this account has finished.",
                ex);
        }

        // ---- past the commit point: ARM has accepted the create. CancellationToken.None, not the
        // caller's: a client that hangs up in this gap would otherwise throw before the service's audit
        // row is written, which is the accepted-but-unaudited case the commit-point rule exists to
        // prevent (#211 review).
        return await GetDeploymentAsync(request.AccountName, request.DeploymentName, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Deployment '{request.DeploymentName}' in account '{request.AccountName}' was accepted by Azure but could not be read back.");
    }

    /// <inheritdoc />
    public async Task<FoundryDeploymentResponse> UpdateCapacityAsync(string accountName, string deploymentName, string skuName, int capacity, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(skuName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        var deployment = armClient.GetCognitiveServicesAccountDeploymentResource(
            CognitiveServicesAccountDeploymentResource.CreateResourceIdentifier(
                RequiredSubscriptionId, RequiredResourceGroup, accountName, deploymentName));

        // PatchResourceTagsAndSku is the body of Deployments_Update — a PATCH carrying { sku, tags }
        // and nothing else. Same WaitUntil.Started reasoning as create: ARM may report Updating for a
        // while and an HTTP request must not block on it.
        var patch = new PatchResourceTagsAndSku
        {
            Sku = new CognitiveServicesSku(skuName) { Capacity = capacity },
        };

        try
        {
            var operation = await deployment.UpdateAsync(WaitUntil.Started, patch, cancellationToken).ConfigureAwait(false);

            // As in CreateDeploymentAsync: false in practice under WaitUntil.Started, kept for the
            // contract.
            if (operation.HasValue)
            {
                return Map(accountName, operation.Value.Data);
            }
        }
        catch (RequestFailedException ex) when (IsAccountMissing(ex))
        {
            throw new FoundryAccountNotFoundException(accountName, ex);
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            throw new KeyNotFoundException($"Deployment '{deploymentName}' was not found in Foundry account '{accountName}'.");
        }

        // Same commit point as the create above: ARM has taken the PATCH, so the read-back that turns it
        // into a response must not be abandoned by the caller's token (#211 review).
        return await GetDeploymentAsync(accountName, deploymentName, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Deployment '{deploymentName}' in account '{accountName}' was resized by Azure but could not be read back.");
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        var deployment = armClient.GetCognitiveServicesAccountDeploymentResource(
            CognitiveServicesAccountDeploymentResource.CreateResourceIdentifier(
                RequiredSubscriptionId, RequiredResourceGroup, accountName, deploymentName));

        try
        {
            _ = await deployment.DeleteAsync(WaitUntil.Started, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (IsAccountMissing(ex))
        {
            throw new FoundryAccountNotFoundException(accountName, ex);
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static bool IsAccountMissing(RequestFailedException ex) =>
        ex.Status == (int)HttpStatusCode.NotFound
        && string.Equals(ex.ErrorCode, ParentResourceNotFoundCode, StringComparison.OrdinalIgnoreCase);

    private string RequiredSubscriptionId =>
        appSettings.Gateway.SubscriptionId
        ?? throw new InvalidOperationException("Gateway:SubscriptionId is not configured.");

    private string RequiredResourceGroup =>
        appSettings.Gateway.ResourceGroup
        ?? throw new InvalidOperationException("Gateway:ResourceGroup is not configured.");

    private CognitiveServicesAccountDeploymentCollection Deployments(string accountName) =>
        armClient
            .GetCognitiveServicesAccountResource(
                CognitiveServicesAccountResource.CreateResourceIdentifier(RequiredSubscriptionId, RequiredResourceGroup, accountName))
            .GetCognitiveServicesAccountDeployments();

    private static FoundryDeploymentResponse Map(string accountName, CognitiveServicesAccountDeploymentData data) =>
        new(
            accountName,
            data.Name,
            data.Properties?.Model?.Format ?? string.Empty,
            data.Properties?.Model?.Name ?? string.Empty,
            data.Properties?.Model?.Version,
            data.Sku?.Name ?? string.Empty,
            data.Sku?.Capacity,
            data.Properties?.ProvisioningState?.ToString() ?? string.Empty,
            data.SystemData?.CreatedOn,
            data.SystemData?.LastModifiedOn);
}
