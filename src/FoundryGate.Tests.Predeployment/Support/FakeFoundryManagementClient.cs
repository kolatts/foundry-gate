using FoundryGate.Api.Services.Foundry;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Foundry.Contracts;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// In-memory <see cref="IFoundryManagementClient"/>: a dictionary of accounts → deployments that
/// behaves like the ARM seam's contract (unknown account → <see cref="KeyNotFoundException"/> on
/// list, absent deployment → <see langword="null"/>/<see langword="false"/>, existing name on
/// create → <see cref="ConflictException"/>) and records every mutation so tests can assert the
/// service never re-PUTs, never recreates, and never reaches ARM when it should refuse first.
/// No live Azure in tests (CLAUDE.md) — this is the only Foundry client the test host ever sees.
/// </summary>
public sealed class FakeFoundryManagementClient : IFoundryManagementClient
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, Dictionary<string, FoundryDeploymentResponse>> _accounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every create the service asked for, in order.</summary>
    public List<CreateFoundryDeploymentRequest> CreateCalls { get; } = [];

    /// <summary>Every delete the service asked for, in order.</summary>
    public List<(string AccountName, string DeploymentName)> DeleteCalls { get; } = [];

    /// <summary>State a freshly created deployment reports — ARM's initial response is typically <c>Creating</c>.</summary>
    public string CreatedProvisioningState { get; set; } = "Creating";

    /// <summary>When set, <see cref="CreateDeploymentAsync"/> throws this instead of creating — simulates an ARM rejection.</summary>
    public Exception? ThrowOnCreate { get; set; }

    /// <summary>Makes <paramref name="accountName"/> a known (possibly empty) account.</summary>
    public void AddAccount(string accountName)
    {
        if (!_accounts.ContainsKey(accountName))
        {
            _accounts[accountName] = new Dictionary<string, FoundryDeploymentResponse>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Puts a deployment into <paramref name="accountName"/> (adding the account if needed) and returns it.</summary>
    public FoundryDeploymentResponse Seed(
        string accountName,
        string deploymentName,
        string modelFormat = "OpenAI",
        string modelName = "gpt-4.1-mini",
        string? modelVersion = "2025-04-14",
        string skuName = "GlobalStandard",
        int? capacity = 10,
        string provisioningState = "Succeeded")
    {
        AddAccount(accountName);
        var deployment = new FoundryDeploymentResponse(
            accountName,
            deploymentName,
            modelFormat,
            modelName,
            modelVersion,
            skuName,
            capacity,
            provisioningState,
            SeedTime,
            SeedTime);
        _accounts[accountName][deploymentName] = deployment;
        return deployment;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FoundryDeploymentResponse>> ListDeploymentsAsync(string accountName, CancellationToken cancellationToken)
    {
        if (!_accounts.TryGetValue(accountName, out var deployments))
        {
            throw new KeyNotFoundException($"Fake: Foundry account '{accountName}' does not exist.");
        }

        return Task.FromResult<IReadOnlyList<FoundryDeploymentResponse>>(deployments.Values.ToList());
    }

    /// <inheritdoc />
    public Task<FoundryDeploymentResponse?> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        var found = _accounts.TryGetValue(accountName, out var deployments) && deployments.TryGetValue(deploymentName, out var deployment)
            ? deployment
            : null;
        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<FoundryDeploymentResponse> CreateDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CreateCalls.Add(request);

        if (ThrowOnCreate is not null)
        {
            throw ThrowOnCreate;
        }

        AddAccount(request.AccountName);
        if (_accounts[request.AccountName].ContainsKey(request.DeploymentName))
        {
            throw new ConflictException($"Fake: deployment '{request.DeploymentName}' already exists in '{request.AccountName}' (ARM 409).");
        }

        var created = Seed(
            request.AccountName,
            request.DeploymentName,
            request.ModelFormat.ToString(),
            request.ModelName,
            request.ModelVersion,
            request.SkuName,
            request.Capacity,
            CreatedProvisioningState);
        return Task.FromResult(created);
    }

    /// <inheritdoc />
    public Task<bool> DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        DeleteCalls.Add((accountName, deploymentName));
        var removed = _accounts.TryGetValue(accountName, out var deployments) && deployments.Remove(deploymentName);
        return Task.FromResult(removed);
    }
}
