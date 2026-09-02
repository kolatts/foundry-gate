using FoundryGate.Api.Services.Foundry;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Foundry.Contracts;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// In-memory <see cref="IFoundryManagementClient"/>: a dictionary of accounts → deployments that
/// behaves like the ARM seam's contract (unknown account → <see cref="FoundryAccountNotFoundException"/>
/// from every method, absent deployment → <see langword="null"/>/<see langword="false"/>, existing
/// name on create → <see cref="ConflictException"/>) and records every call so tests can assert the
/// service never re-PUTs, never recreates, never reaches ARM when it should refuse first, and hits
/// the cache when it should. No live Azure in tests (CLAUDE.md) — this is the only Foundry client
/// the test host ever sees.
/// </summary>
public sealed class FakeFoundryManagementClient : IFoundryManagementClient
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, Dictionary<string, FoundryDeploymentResponse>> _accounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every account the service asked to list, in order.</summary>
    public List<string> ListCalls { get; } = [];

    /// <summary>Every create the service asked for, in order.</summary>
    public List<CreateFoundryDeploymentRequest> CreateCalls { get; } = [];

    /// <summary>Every delete the service asked for, in order.</summary>
    public List<(string AccountName, string DeploymentName)> DeleteCalls { get; } = [];

    /// <summary>Every capacity PATCH the service asked for, in order — including the sku name it echoed back (#130).</summary>
    public List<(string AccountName, string DeploymentName, string SkuName, int Capacity)> CapacityCalls { get; } = [];

    /// <summary>State a freshly created deployment reports — ARM's initial response is typically <c>Creating</c>.</summary>
    public string CreatedProvisioningState { get; set; } = "Creating";

    /// <summary>State a freshly resized deployment reports — ARM applies capacity asynchronously.</summary>
    public string UpdatedProvisioningState { get; set; } = "Updating";

    /// <summary>When set, <see cref="UpdateCapacityAsync"/> throws this instead of resizing — simulates ARM refusing a quota increase.</summary>
    public Exception? ThrowOnUpdateCapacity { get; set; }

    /// <summary>Runs inside <see cref="UpdateCapacityAsync"/> after the call is recorded — e.g. to cancel the request token "while ARM was working".</summary>
    public Action<string, string>? OnUpdateCapacity { get; set; }

    /// <summary>When set, <see cref="CreateDeploymentAsync"/> throws this instead of creating — simulates an ARM rejection.</summary>
    public Exception? ThrowOnCreate { get; set; }

    /// <summary>Runs inside <see cref="CreateDeploymentAsync"/> after the create is recorded — e.g. to cancel the request token "while ARM was working".</summary>
    public Action<CreateFoundryDeploymentRequest>? OnCreate { get; set; }

    /// <summary>Runs inside <see cref="DeleteDeploymentAsync"/> after the delete is recorded.</summary>
    public Action<string, string>? OnDelete { get; set; }

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
        ListCalls.Add(accountName);
        return Task.FromResult<IReadOnlyList<FoundryDeploymentResponse>>(RequireAccount(accountName).Values.ToList());
    }

    /// <inheritdoc />
    public Task<FoundryDeploymentResponse?> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        var found = RequireAccount(accountName).TryGetValue(deploymentName, out var deployment) ? deployment : null;
        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<FoundryDeploymentResponse> CreateDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CreateCalls.Add(request);
        OnCreate?.Invoke(request);

        if (ThrowOnCreate is not null)
        {
            throw ThrowOnCreate;
        }

        if (RequireAccount(request.AccountName).ContainsKey(request.DeploymentName))
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
    public Task<FoundryDeploymentResponse> UpdateCapacityAsync(string accountName, string deploymentName, string skuName, int capacity, CancellationToken cancellationToken)
    {
        CapacityCalls.Add((accountName, deploymentName, skuName, capacity));
        OnUpdateCapacity?.Invoke(accountName, deploymentName);

        if (ThrowOnUpdateCapacity is not null)
        {
            throw ThrowOnUpdateCapacity;
        }

        var deployments = RequireAccount(accountName);
        if (!deployments.TryGetValue(deploymentName, out var existing))
        {
            throw new KeyNotFoundException($"Fake: deployment '{deploymentName}' was not found in '{accountName}'.");
        }

        // ARM reports Updating while it applies the new capacity, the same way a create reports
        // Creating; the model, version and name are untouched because the PATCH body cannot carry them.
        var updated = existing with { Capacity = capacity, SkuName = skuName, ProvisioningState = UpdatedProvisioningState };
        deployments[deploymentName] = updated;
        return Task.FromResult(updated);
    }

    /// <inheritdoc />
    public Task<bool> DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken)
    {
        DeleteCalls.Add((accountName, deploymentName));
        OnDelete?.Invoke(accountName, deploymentName);
        return Task.FromResult(RequireAccount(accountName).Remove(deploymentName));
    }

    private Dictionary<string, FoundryDeploymentResponse> RequireAccount(string accountName) =>
        _accounts.TryGetValue(accountName, out var deployments)
            ? deployments
            : throw new FoundryAccountNotFoundException(accountName);
}
