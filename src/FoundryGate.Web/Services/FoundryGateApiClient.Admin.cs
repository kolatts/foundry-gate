using FoundryGate.Domain.Common;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests.Contracts;
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

    public Task<ApiCallResult<UserDetailResponse>> GetUserDetailAsync(int userId, CancellationToken ct = default) =>
        GetAsync<UserDetailResponse>($"users/{userId}", ct);

    // Groups — api.md §Groups

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, bool force, CancellationToken ct = default) =>
        SendActionAsync(HttpMethod.Delete, force ? $"groups/{groupId}?force=true" : $"groups/{groupId}", body: null, ct);

    // Quota — api.md §Quota

    public Task<ApiCallResult<IReadOnlyList<QuotaTierResponse>>> GetQuotaTiersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<QuotaTierResponse>>("quota/tiers", ct);

    // Quota increase requests — api.md §Quota Increase Requests

    public Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(QuotaRequestQuery query, PagedRequest paging, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(paging);

        var parts = PagingParts(paging);
        if (query.Status is { } status)
        {
            // The API binds ?status= as the QuotaRequestStatusType's numeric value (no string
            // enum converter is configured on either side) — see api.md's "0 Pending, 1 Approved,
            // 2 Rejected".
            parts.Add($"status={(int)status}");
        }

        if (query.UserId is { } userId)
        {
            parts.Add($"userId={userId}");
        }

        return GetAsync<PagedResult<QuotaIncreaseRequestResponse>>($"requests?{string.Join('&', parts)}", ct);
    }

    // Foundry — api.md §Foundry

    public Task<ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>> GetFoundryDeploymentsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FoundryDeploymentResponse>>("foundry/deployments", ct);

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
