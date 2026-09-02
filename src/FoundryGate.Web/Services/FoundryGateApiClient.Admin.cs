using FoundryGate.Domain.Common;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Web.Services;

/// <inheritdoc cref="IFoundryGateApiClient" />
/// <remarks>
/// The admin-page half of the client (#51, #52, #53, #62, #63) — see
/// <c>IFoundryGateApiClient.Admin.cs</c> for why it is a separate partial file. Every method
/// reuses the HTTP plumbing (<c>SendAsync</c>/<c>SendActionAsync</c> and their
/// <see cref="ApiCallResult{T}"/> mapping) declared in <c>FoundryGateApiClient.cs</c>.
/// </remarks>
public sealed partial class FoundryGateApiClient
{
    // Users — api.md §Users

    public Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(UserListQuery query, PagedRequest paging, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(paging);

        var parts = PagingParts(paging);
        AddIfPresent(parts, "search", query.Search);
        if (query.IsActive is { } isActive)
        {
            parts.Add($"isActive={(isActive ? "true" : "false")}");
        }

        return GetAsync<PagedResult<UserResponse>>($"users?{string.Join('&', parts)}", ct);
    }

    public Task<ApiCallResult<UserSyncStatusResponse>> GetLastUserSyncAsync(CancellationToken ct = default) =>
        GetAsync<UserSyncStatusResponse>("users/sync/last", ct);

    // Groups — api.md §Groups

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, bool force, CancellationToken ct = default) =>
        SendActionAsync(HttpMethod.Delete, force ? $"groups/{groupId}?force=true" : $"groups/{groupId}", body: null, ct);

    // Foundry — api.md §Foundry

    public Task<ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>> GetFoundryDeploymentsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FoundryDeploymentResponse>>("foundry/deployments", ct);

    public Task<ApiCallResult<IReadOnlyList<FoundryCatalogEntryResponse>>> GetFoundryCatalogAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FoundryCatalogEntryResponse>>("foundry/catalog", ct);

    public Task<ApiCallResult<FoundryDeploymentResponse>> CreateFoundryDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<FoundryDeploymentResponse>(HttpMethod.Post, "foundry/deployments", request, ct);
    }

    public Task<ApiCallResult<bool>> DeleteFoundryDeploymentAsync(string accountName, string deploymentName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        return SendActionAsync(
            HttpMethod.Delete,
            $"foundry/deployments/{Uri.EscapeDataString(accountName)}/{Uri.EscapeDataString(deploymentName)}",
            body: null,
            ct);
    }

    // ── Query-string helpers ───────────────────────────────────────────────────

    private static List<string> PagingParts(PagedRequest paging)
    {
        var clamped = paging.Clamp();
        return [$"page={clamped.Page}", $"pageSize={clamped.PageSize}"];
    }

    private static void AddIfPresent(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}
