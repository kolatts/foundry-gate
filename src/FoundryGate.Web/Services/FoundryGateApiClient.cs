using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Dashboard.Contracts;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace FoundryGate.Web.Services;

/// <inheritdoc cref="IFoundryGateApiClient" />
/// <remarks>
/// Registered in Program.cs behind <see cref="IFoundryGateApiClient"/> with an
/// <see cref="HttpClient"/> built by hand: <c>BaseAddress</c> set from
/// <c>Api:BaseUrl</c>, wrapped in MSAL's <c>AuthorizationMessageHandler</c> so the bearer
/// token is attached automatically — every method here only needs to know its own
/// relative route.
/// </remarks>
public sealed partial class FoundryGateApiClient(HttpClient httpClient) : IFoundryGateApiClient
{
    // Users — spec §4.1
    public Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(PagedRequest paging, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paging);
        return GetAsync<PagedResult<UserResponse>>(WithPaging("users", paging), ct);
    }

    public Task<ApiCallResult<UserProfileResponse>> GetMeAsync(CancellationToken ct = default) =>
        GetAsync<UserProfileResponse>("users/me", ct);

    public Task<ApiCallResult<UserResponse>> GetUserAsync(int userId, CancellationToken ct = default) =>
        GetAsync<UserResponse>($"users/{userId}", ct);

    public Task<ApiCallResult<bool>> UpdateUserQuotaAsync(int userId, UpdateUserQuotaRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendActionAsync(HttpMethod.Put, $"users/{userId}/quota", request, ct);
    }

    public Task<ApiCallResult<bool>> ActivateUserAsync(int userId, CancellationToken ct = default) =>
        SendActionAsync(HttpMethod.Post, $"users/{userId}/activate", body: null, ct);

    public Task<ApiCallResult<bool>> DeactivateUserAsync(int userId, CancellationToken ct = default) =>
        SendActionAsync(HttpMethod.Post, $"users/{userId}/deactivate", body: null, ct);

    public Task<ApiCallResult<UserSyncResult>> SyncUsersAsync(CancellationToken ct = default) =>
        SendAsync<UserSyncResult>(HttpMethod.Post, "users/sync", body: null, ct);

    // Groups — spec §4.2
    public Task<ApiCallResult<PagedResult<GroupResponse>>> GetGroupsAsync(PagedRequest paging, string? search = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paging);
        var path = WithPaging("groups", paging);
        if (!string.IsNullOrWhiteSpace(search))
        {
            path += $"&search={Uri.EscapeDataString(search)}";
        }

        return GetAsync<PagedResult<GroupResponse>>(path, ct);
    }

    public Task<ApiCallResult<GroupResponse>> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<GroupResponse>(HttpMethod.Post, "groups", request, ct);
    }

    public Task<ApiCallResult<GroupDetailResponse>> GetGroupAsync(int groupId, CancellationToken ct = default) =>
        GetAsync<GroupDetailResponse>($"groups/{groupId}", ct);

    public Task<ApiCallResult<bool>> UpdateGroupAsync(int groupId, UpdateGroupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendActionAsync(HttpMethod.Put, $"groups/{groupId}", request, ct);
    }

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, CancellationToken ct = default) =>
        SendActionAsync(HttpMethod.Delete, $"groups/{groupId}", body: null, ct);

    public Task<ApiCallResult<bool>> AddGroupMemberAsync(int groupId, AddGroupMemberRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendActionAsync(HttpMethod.Post, $"groups/{groupId}/members", request, ct);
    }

    public Task<ApiCallResult<bool>> RemoveGroupMemberAsync(int groupId, int userId, CancellationToken ct = default) =>
        SendActionAsync(HttpMethod.Delete, $"groups/{groupId}/members/{userId}", body: null, ct);

    public Task<ApiCallResult<PagedResult<GroupMemberResponse>>> GetGroupMembersAsync(int groupId, PagedRequest paging, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paging);
        return GetAsync<PagedResult<GroupMemberResponse>>(WithPaging($"groups/{groupId}/members", paging), ct);
    }

    public Task<ApiCallResult<GroupSyncResult>> SyncGroupFromEntraAsync(int groupId, CancellationToken ct = default) =>
        SendAsync<GroupSyncResult>(HttpMethod.Post, $"groups/{groupId}/sync-entra", body: null, ct);

    public Task<ApiCallResult<IReadOnlyList<GroupSyncResult>>> SyncGroupsFromEntraAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<GroupSyncResult>>(HttpMethod.Post, "groups/sync-entra", body: null, ct);

    // Quota — spec §4.3
    public Task<ApiCallResult<PagedResult<QuotaAllocationResponse>>> GetQuotaAllocationsAsync(PagedRequest paging, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paging);
        return GetAsync<PagedResult<QuotaAllocationResponse>>(WithPaging("quota/allocations", paging), ct);
    }

    public Task<ApiCallResult<QuotaAllocationResponse>> GetMyQuotaAllocationAsync(CancellationToken ct = default) =>
        GetAsync<QuotaAllocationResponse>("quota/allocations/me", ct);

    public Task<ApiCallResult<QuotaAllocationResponse>> GetUserQuotaAllocationAsync(int userId, CancellationToken ct = default) =>
        GetAsync<QuotaAllocationResponse>($"quota/allocations/{userId}", ct);

    public Task<ApiCallResult<QuotaResetResult>> ResetQuotaAsync(CancellationToken ct = default) =>
        SendAsync<QuotaResetResult>(HttpMethod.Post, "quota/reset", body: null, ct);

    // Quota increase requests — spec §4.4
    public Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(PagedRequest paging, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paging);
        return GetAsync<PagedResult<QuotaIncreaseRequestResponse>>(WithPaging("requests", paging), ct);
    }

    public Task<ApiCallResult<QuotaIncreaseRequestResponse>> SubmitRequestAsync(SubmitQuotaIncreaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<QuotaIncreaseRequestResponse>(HttpMethod.Post, "requests", request, ct);
    }

    public Task<ApiCallResult<QuotaIncreaseRequestResponse>> GetRequestAsync(int requestId, CancellationToken ct = default) =>
        GetAsync<QuotaIncreaseRequestResponse>($"requests/{requestId}", ct);

    public Task<ApiCallResult<bool>> ApproveRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendActionAsync(HttpMethod.Post, $"requests/{requestId}/approve", request, ct);
    }

    public Task<ApiCallResult<bool>> RejectRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendActionAsync(HttpMethod.Post, $"requests/{requestId}/reject", request, ct);
    }

    // Keys — spec §4.5
    public Task<ApiCallResult<ApiKeyResponse>> GetMyKeyAsync(CancellationToken ct = default) =>
        GetAsync<ApiKeyResponse>("keys/me", ct);

    public Task<ApiCallResult<ApiKeyRevealResponse>> RotateMyKeyAsync(CancellationToken ct = default) =>
        SendAsync<ApiKeyRevealResponse>(HttpMethod.Post, "keys/me/rotate", body: null, ct);

    public Task<ApiCallResult<ApiKeyRevealResponse>> RotateUserKeyAsync(int userId, CancellationToken ct = default) =>
        SendAsync<ApiKeyRevealResponse>(HttpMethod.Post, $"keys/{userId}/rotate", body: null, ct);

    public Task<ApiCallResult<ApiKeyRevealResponse>> ProvisionUserKeyAsync(int userId, CancellationToken ct = default) =>
        SendAsync<ApiKeyRevealResponse>(HttpMethod.Post, $"keys/{userId}/provision", body: null, ct);

    public Task<ApiCallResult<bool>> RevokeUserKeyAsync(int userId, CancellationToken ct = default) =>
        SendActionAsync(HttpMethod.Delete, $"keys/{userId}", body: null, ct);

    // Admin / configuration — spec §4.6
    public Task<ApiCallResult<IReadOnlyList<SystemConfigEntryResponse>>> GetConfigAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<SystemConfigEntryResponse>>("config", ct);

    public Task<ApiCallResult<bool>> UpdateConfigAsync(string key, UpdateSystemConfigRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(request);
        return SendActionAsync(HttpMethod.Put, $"config/{Uri.EscapeDataString(key)}", request, ct);
    }

    public Task<ApiCallResult<PagedResult<AuditLogEntryResponse>>> GetAuditLogAsync(AuditLogQuery query, PagedRequest paging, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(paging);
        return GetAsync<PagedResult<AuditLogEntryResponse>>(BuildAuditLogQuery(query, paging), ct);
    }

    public Task<ApiCallResult<DashboardSummaryResponse>> GetDashboardAsync(CancellationToken ct = default) =>
        GetAsync<DashboardSummaryResponse>("dashboard", ct);

    // ── HTTP plumbing ──────────────────────────────────────────────────────────

    private Task<ApiCallResult<T>> GetAsync<T>(string requestUri, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Get, requestUri, body: null, ct);

    private async Task<ApiCallResult<T>> SendAsync<T>(HttpMethod method, string requestUri, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, requestUri);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return await ReadSuccessAsync<T>(response, ct);
            }

            return await ReadFailureAsync<T>(response, ct);
        }
        catch (AccessTokenNotAvailableException)
        {
            // MSAL's AuthorizationMessageHandler throws this when it can't silently
            // acquire/refresh a token (expired session, blocked third-party cookies, ...).
            // Deliberately NOT calling ex.Redirect() here: this is a data client, not a
            // navigation surface — the caller decides what "sign in again" looks like from
            // the Unauthorized status (see Pages/Home.razor).
            return ApiCallResult<T>.Fail(ApiCallStatus.Unauthorized, FriendlyMessage(ApiCallStatus.Unauthorized, error: null));
        }
        catch (HttpRequestException ex)
        {
            return ApiCallResult<T>.Fail(ApiCallStatus.Unavailable, UnavailableMessage, TransportError(ex));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient surfaces its own request timeout as TaskCanceledException, not
            // OperationCanceledException tied to our token.
            return ApiCallResult<T>.Fail(ApiCallStatus.Unavailable, UnavailableMessage);
        }
    }

    /// <summary>
    /// For admin/action endpoints whose success case is "it worked" rather than a
    /// meaningful response body (activate/deactivate/approve/reject/delete/...).
    /// </summary>
    private async Task<ApiCallResult<bool>> SendActionAsync(HttpMethod method, string requestUri, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, requestUri);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? ApiCallResult<bool>.Ok(true)
                : await ReadFailureAsync<bool>(response, ct);
        }
        catch (AccessTokenNotAvailableException)
        {
            // See the matching catch in SendAsync<T> — same reasoning, no ex.Redirect().
            return ApiCallResult<bool>.Fail(ApiCallStatus.Unauthorized, FriendlyMessage(ApiCallStatus.Unauthorized, error: null));
        }
        catch (HttpRequestException ex)
        {
            return ApiCallResult<bool>.Fail(ApiCallStatus.Unavailable, UnavailableMessage, TransportError(ex));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiCallResult<bool>.Fail(ApiCallStatus.Unavailable, UnavailableMessage);
        }
    }

    private static async Task<ApiCallResult<T>> ReadSuccessAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
            return value is null
                ? ApiCallResult<T>.Fail(ApiCallStatus.Unavailable, "Foundry Gate's API returned an empty response.")
                : ApiCallResult<T>.Ok(value);
        }
        catch (JsonException)
        {
            return ApiCallResult<T>.Fail(ApiCallStatus.Unavailable, "Foundry Gate's API returned a response that couldn't be read.");
        }
    }

    private static async Task<ApiCallResult<T>> ReadFailureAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var error = await TryReadErrorAsync(response, ct);
        var status = MapStatus(response.StatusCode);
        return ApiCallResult<T>.Fail(status, FriendlyMessage(status, error), error);
    }

    private static async Task<ApiError?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken: ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ApiCallStatus MapStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => ApiCallStatus.Unauthorized,
        HttpStatusCode.Forbidden => ApiCallStatus.Forbidden,
        HttpStatusCode.NotFound => ApiCallStatus.NotFound,
        _ => ApiCallStatus.Error,
    };

    private static string FriendlyMessage(ApiCallStatus status, ApiError? error) => status switch
    {
        ApiCallStatus.Unauthorized => "Your sign-in has expired. Please sign in again.",
        ApiCallStatus.Forbidden => "You don't have permission to do that.",
        ApiCallStatus.NotFound => "That wasn't found.",
        _ => error?.Detail ?? error?.Title ?? "Something went wrong talking to Foundry Gate's API.",
    };

    private const string UnavailableMessage = "Foundry Gate's API isn't reachable right now. Please try again shortly.";

    private static ApiError TransportError(HttpRequestException ex) =>
        new(ApiError.DefaultType, "API unavailable", 0, ex.Message);

    private static string WithPaging(string path, PagedRequest paging)
    {
        var clamped = paging.Clamp();
        return $"{path}?page={clamped.Page}&pageSize={clamped.PageSize}";
    }

    private static string BuildAuditLogQuery(AuditLogQuery query, PagedRequest paging)
    {
        var clamped = paging.Clamp();
        List<string> parts = [$"page={clamped.Page}", $"pageSize={clamped.PageSize}"];

        if (query.ActorUserId is { } actorUserId)
        {
            parts.Add($"actorUserId={actorUserId}");
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            parts.Add($"action={Uri.EscapeDataString(query.Action)}");
        }

        if (!string.IsNullOrWhiteSpace(query.TargetType))
        {
            parts.Add($"targetType={Uri.EscapeDataString(query.TargetType)}");
        }

        if (!string.IsNullOrWhiteSpace(query.TargetId))
        {
            parts.Add($"targetId={Uri.EscapeDataString(query.TargetId)}");
        }

        if (query.FromDate is { } fromDate)
        {
            parts.Add($"fromDate={Uri.EscapeDataString(fromDate.ToString("O"))}");
        }

        if (query.ToDate is { } toDate)
        {
            parts.Add($"toDate={Uri.EscapeDataString(toDate.ToString("O"))}");
        }

        return $"audit?{string.Join('&', parts)}";
    }
}
