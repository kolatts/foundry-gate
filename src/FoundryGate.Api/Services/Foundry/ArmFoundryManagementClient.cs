using System.Net;
using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Models;
using FoundryGate.Api.Configuration;
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
            throw new KeyNotFoundException($"Foundry account '{accountName}' was not found in resource group '{appSettings.Gateway.ResourceGroup}'.", ex);
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

            // A PUT's initial response normally carries the resource (Accepted/Creating); when the SDK
            // hasn't materialized it, read the deployment back rather than waiting for the LRO.
            if (operation.HasValue)
            {
                return Map(request.AccountName, operation.Value.Data);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Conflict)
        {
            throw new ConflictException(
                $"Azure refused to create deployment '{request.DeploymentName}' in account '{request.AccountName}' (409): {ex.ErrorCode}. " +
                "Deployment writes are serialized per account — retry once any in-flight create on this account has finished.",
                ex);
        }

        return await GetDeploymentAsync(request.AccountName, request.DeploymentName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Deployment '{request.DeploymentName}' in account '{request.AccountName}' was accepted by Azure but could not be read back.");
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
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }

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
