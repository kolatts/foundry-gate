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

    private readonly Dictionary<string, List<FoundryCatalogEntryResponse>> _catalogs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every account the service asked to list, in order.</summary>
    public List<string> ListCalls { get; } = [];

    /// <summary>Every account the service asked for a catalogue, in order — how a test proves the 5-minute cache is doing its job.</summary>
    public List<string> CatalogCalls { get; } = [];

    /// <summary>Every create the service asked for, in order.</summary>
    public List<CreateFoundryDeploymentRequest> CreateCalls { get; } = [];

    /// <summary>Every delete the service asked for, in order.</summary>
    public List<(string AccountName, string DeploymentName)> DeleteCalls { get; } = [];

    /// <summary>State a freshly created deployment reports — ARM's initial response is typically <c>Creating</c>.</summary>
    public string CreatedProvisioningState { get; set; } = "Creating";

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

    /// <summary>
    /// Adds a model to <paramref name="accountName"/>'s deployable catalogue (adding the account if
    /// needed). <paramref name="skuNames"/> is in ARM's own order, so the first is the default SKU the
    /// real client would report — and <paramref name="defaultCapacity"/> belongs to <em>that</em> SKU.
    /// </summary>
    public void SeedCatalog(
        string accountName,
        string modelName,
        string modelVersion = "2025-04-14",
        string modelFormat = "OpenAI",
        int? defaultCapacity = 10,
        bool isDefaultVersion = true,
        string lifecycleStatus = "GenerallyAvailable",
        DateTimeOffset? inferenceRetiresOn = null,
        params string[] skuNames)
    {
        ArgumentNullException.ThrowIfNull(skuNames);

        AddAccount(accountName);
        if (!_catalogs.TryGetValue(accountName, out var catalog))
        {
            catalog = [];
            _catalogs[accountName] = catalog;
        }

        string[] skus = skuNames.Length == 0 ? ["GlobalStandard"] : skuNames;

        catalog.Add(new FoundryCatalogEntryResponse(
            modelFormat,
            modelName,
            modelVersion,
            // Sorted for display, exactly as the real mapper does — so a test that asserts on the
            // default SKU is asserting on ARM's order, not on the alphabet.
            [.. skus.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            defaultCapacity,
            skus[0],
            isDefaultVersion,
            lifecycleStatus,
            inferenceRetiresOn));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FoundryCatalogEntryResponse>> ListCatalogAsync(string accountName, CancellationToken cancellationToken)
    {
        CatalogCalls.Add(accountName);

        // Same account-missing contract as every other method: an unknown account throws.
        _ = RequireAccount(accountName);

        return Task.FromResult<IReadOnlyList<FoundryCatalogEntryResponse>>(
            _catalogs.TryGetValue(accountName, out var catalog) ? [.. catalog] : []);
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
