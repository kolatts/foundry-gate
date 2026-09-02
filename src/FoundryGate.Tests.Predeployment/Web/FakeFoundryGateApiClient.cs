using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Dashboard.Contracts;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// A hand-rolled <see cref="IFoundryGateApiClient"/> for the Blazor component tests (CONVENTIONS.md:
/// no mocking library). Every method returns a canned <see cref="ApiCallResult{T}"/> from a settable
/// property, so a test overrides only the call it cares about and inherits sensible successes for
/// the rest. Calls are counted by name, and the arguments the pages send are captured, so a test can
/// assert what the page asked for as well as what it rendered.
/// </summary>
/// <remarks>
/// <see cref="Gate"/> holds every call until it is completed — that is how the "submit is disabled
/// while the request is in flight" and "the dashboard is refreshing" states are observed, without a
/// sleep anywhere in the suite.
/// </remarks>
public sealed class FakeFoundryGateApiClient : IFoundryGateApiClient
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

    /// <summary>When set, every call awaits this before returning — release it to let the page finish.</summary>
    public TaskCompletionSource? Gate { get; set; }

    // -- Canned responses -----------------------------------------------------------------------

    public ApiCallResult<UserProfileResponse> MeResult { get; set; } = ApiCallResult<UserProfileResponse>.Ok(WebTestData.Profile());

    public ApiCallResult<IReadOnlyList<FoundryModelResponse>> FoundryModelsResult { get; set; } =
        ApiCallResult<IReadOnlyList<FoundryModelResponse>>.Ok([WebTestData.Model()]);

    public ApiCallResult<IReadOnlyList<QuotaTierResponse>> QuotaTiersResult { get; set; } =
        ApiCallResult<IReadOnlyList<QuotaTierResponse>>.Ok(WebTestData.Tiers());

    public ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>> RequestsResult { get; set; } =
        ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>.Ok(WebTestData.Page<QuotaIncreaseRequestResponse>());

    public ApiCallResult<QuotaIncreaseRequestResponse> SubmitRequestResult { get; set; } =
        ApiCallResult<QuotaIncreaseRequestResponse>.Ok(WebTestData.Request());

    public ApiCallResult<ApiKeyRevealResponse> RevealKeyResult { get; set; } =
        ApiCallResult<ApiKeyRevealResponse>.Ok(WebTestData.Reveal());

    public ApiCallResult<ApiKeyRevealResponse> RotateKeyResult { get; set; } =
        ApiCallResult<ApiKeyRevealResponse>.Ok(WebTestData.Reveal("rotated-key-value"));

    public ApiCallResult<DashboardSummaryResponse> DashboardResult { get; set; } =
        ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard());

    public ApiCallResult<IReadOnlyList<SystemConfigEntryResponse>> ConfigResult { get; set; } =
        ApiCallResult<IReadOnlyList<SystemConfigEntryResponse>>.Ok([WebTestData.ConfigEntry()]);

    public ApiCallResult<bool> UpdateConfigResult { get; set; } = ApiCallResult<bool>.Ok(true);

    /// <summary>Per-key override for <see cref="UpdateConfigAsync"/>; falls back to <see cref="UpdateConfigResult"/>.</summary>
    public Dictionary<string, ApiCallResult<bool>> UpdateConfigResults { get; } = new(StringComparer.Ordinal);

    public ApiCallResult<PagedResult<AuditLogEntryResponse>> AuditResult { get; set; } =
        ApiCallResult<PagedResult<AuditLogEntryResponse>>.Ok(WebTestData.Page<AuditLogEntryResponse>());

    // -- Captured arguments ---------------------------------------------------------------------

    public List<SubmitQuotaIncreaseRequest> SubmittedRequests { get; } = [];

    public List<(string Key, string Value)> ConfigUpdates { get; } = [];

    public List<(AuditLogQuery Query, PagedRequest Paging)> AuditQueries { get; } = [];

    public List<(QuotaRequestQuery Query, PagedRequest Paging)> FilteredRequestQueries { get; } = [];

    /// <summary>How many times the named method was called (method names, e.g. <c>GetDashboardAsync</c>).</summary>
    public int CallCount(string method) => _calls.TryGetValue(method, out var count) ? count : 0;

    // -- IFoundryGateApiClient ------------------------------------------------------------------

    public Task<ApiCallResult<UserProfileResponse>> GetMeAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetMeAsync), MeResult);

    public Task<ApiCallResult<IReadOnlyList<FoundryModelResponse>>> GetFoundryModelsAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetFoundryModelsAsync), FoundryModelsResult);

    public Task<ApiCallResult<IReadOnlyList<QuotaTierResponse>>> GetQuotaTiersAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetQuotaTiersAsync), QuotaTiersResult);

    public Task<ApiCallResult<ApiKeyRevealResponse>> RevealMyKeyAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(RevealMyKeyAsync), RevealKeyResult);

    public Task<ApiCallResult<ApiKeyRevealResponse>> RotateMyKeyAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(RotateMyKeyAsync), RotateKeyResult);

    public Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(PagedRequest paging, CancellationToken ct = default) =>
        RespondAsync(nameof(GetRequestsAsync), RequestsResult);

    public Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(
        QuotaRequestQuery query,
        PagedRequest paging,
        CancellationToken ct = default)
    {
        FilteredRequestQueries.Add((query, paging));
        return RespondAsync(nameof(GetRequestsAsync), RequestsResult);
    }

    public Task<ApiCallResult<QuotaIncreaseRequestResponse>> SubmitRequestAsync(SubmitQuotaIncreaseRequest request, CancellationToken ct = default)
    {
        SubmittedRequests.Add(request);
        return RespondAsync(nameof(SubmitRequestAsync), SubmitRequestResult);
    }

    public Task<ApiCallResult<DashboardSummaryResponse>> GetDashboardAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetDashboardAsync), DashboardResult);

    public Task<ApiCallResult<IReadOnlyList<SystemConfigEntryResponse>>> GetConfigAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetConfigAsync), ConfigResult);

    public Task<ApiCallResult<bool>> UpdateConfigAsync(string key, UpdateSystemConfigRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ConfigUpdates.Add((key, request.Value));
        var result = UpdateConfigResults.TryGetValue(key, out var perKey) ? perKey : UpdateConfigResult;
        return RespondAsync(nameof(UpdateConfigAsync), result);
    }

    public Task<ApiCallResult<PagedResult<AuditLogEntryResponse>>> GetAuditLogAsync(AuditLogQuery query, PagedRequest paging, CancellationToken ct = default)
    {
        AuditQueries.Add((query, paging));
        return RespondAsync(nameof(GetAuditLogAsync), AuditResult);
    }

    // -- Not exercised by these pages; the admin-management wave (#51-#53, #62, #63) fills these in.

    public Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(PagedRequest paging, CancellationToken ct = default) =>
        RespondAsync(nameof(GetUsersAsync), NotStubbed<PagedResult<UserResponse>>());

    public Task<ApiCallResult<UserResponse>> GetUserAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(GetUserAsync), NotStubbed<UserResponse>());

    public Task<ApiCallResult<bool>> UpdateUserQuotaAsync(int userId, UpdateUserQuotaRequest request, CancellationToken ct = default) =>
        RespondAsync(nameof(UpdateUserQuotaAsync), NotStubbed<bool>());

    public Task<ApiCallResult<bool>> ActivateUserAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(ActivateUserAsync), NotStubbed<bool>());

    public Task<ApiCallResult<bool>> DeactivateUserAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(DeactivateUserAsync), NotStubbed<bool>());

    public Task<ApiCallResult<UserSyncResult>> SyncUsersAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(SyncUsersAsync), NotStubbed<UserSyncResult>());

    public Task<ApiCallResult<PagedResult<GroupResponse>>> GetGroupsAsync(PagedRequest paging, string? search = null, CancellationToken ct = default) =>
        RespondAsync(nameof(GetGroupsAsync), NotStubbed<PagedResult<GroupResponse>>());

    public Task<ApiCallResult<GroupResponse>> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default) =>
        RespondAsync(nameof(CreateGroupAsync), NotStubbed<GroupResponse>());

    public Task<ApiCallResult<GroupDetailResponse>> GetGroupAsync(int groupId, CancellationToken ct = default) =>
        RespondAsync(nameof(GetGroupAsync), NotStubbed<GroupDetailResponse>());

    public Task<ApiCallResult<bool>> UpdateGroupAsync(int groupId, UpdateGroupRequest request, CancellationToken ct = default) =>
        RespondAsync(nameof(UpdateGroupAsync), NotStubbed<bool>());

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, CancellationToken ct = default) =>
        RespondAsync(nameof(DeleteGroupAsync), NotStubbed<bool>());

    public Task<ApiCallResult<bool>> AddGroupMemberAsync(int groupId, AddGroupMemberRequest request, CancellationToken ct = default) =>
        RespondAsync(nameof(AddGroupMemberAsync), NotStubbed<bool>());

    public Task<ApiCallResult<bool>> RemoveGroupMemberAsync(int groupId, int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(RemoveGroupMemberAsync), NotStubbed<bool>());

    public Task<ApiCallResult<PagedResult<GroupMemberResponse>>> GetGroupMembersAsync(int groupId, PagedRequest paging, CancellationToken ct = default) =>
        RespondAsync(nameof(GetGroupMembersAsync), NotStubbed<PagedResult<GroupMemberResponse>>());

    public Task<ApiCallResult<GroupSyncResult>> SyncGroupFromEntraAsync(int groupId, CancellationToken ct = default) =>
        RespondAsync(nameof(SyncGroupFromEntraAsync), NotStubbed<GroupSyncResult>());

    public Task<ApiCallResult<IReadOnlyList<GroupSyncResult>>> SyncGroupsFromEntraAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(SyncGroupsFromEntraAsync), NotStubbed<IReadOnlyList<GroupSyncResult>>());

    public Task<ApiCallResult<PagedResult<QuotaAllocationResponse>>> GetQuotaAllocationsAsync(PagedRequest paging, CancellationToken ct = default) =>
        RespondAsync(nameof(GetQuotaAllocationsAsync), NotStubbed<PagedResult<QuotaAllocationResponse>>());

    public Task<ApiCallResult<QuotaAllocationResponse>> GetMyQuotaAllocationAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetMyQuotaAllocationAsync), ApiCallResult<QuotaAllocationResponse>.Ok(WebTestData.Allocation()));

    public Task<ApiCallResult<QuotaAllocationResponse>> GetUserQuotaAllocationAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(GetUserQuotaAllocationAsync), NotStubbed<QuotaAllocationResponse>());

    public Task<ApiCallResult<QuotaResetResult>> ResetQuotaAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(ResetQuotaAsync), NotStubbed<QuotaResetResult>());

    public Task<ApiCallResult<QuotaIncreaseRequestResponse>> GetRequestAsync(int requestId, CancellationToken ct = default) =>
        RespondAsync(nameof(GetRequestAsync), NotStubbed<QuotaIncreaseRequestResponse>());

    public Task<ApiCallResult<bool>> ApproveRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default) =>
        RespondAsync(nameof(ApproveRequestAsync), NotStubbed<bool>());

    public Task<ApiCallResult<bool>> RejectRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default) =>
        RespondAsync(nameof(RejectRequestAsync), NotStubbed<bool>());

    public Task<ApiCallResult<ApiKeyResponse>> GetMyKeyAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetMyKeyAsync), ApiCallResult<ApiKeyResponse>.Ok(WebTestData.Key()));

    public Task<ApiCallResult<ApiKeyRevealResponse>> RotateUserKeyAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(RotateUserKeyAsync), NotStubbed<ApiKeyRevealResponse>());

    public Task<ApiCallResult<ApiKeyRevealResponse>> ProvisionUserKeyAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(ProvisionUserKeyAsync), NotStubbed<ApiKeyRevealResponse>());

    public Task<ApiCallResult<bool>> RevokeUserKeyAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(RevokeUserKeyAsync), NotStubbed<bool>());

    // -- Plumbing -------------------------------------------------------------------------------

    private async Task<ApiCallResult<T>> RespondAsync<T>(string method, ApiCallResult<T> result)
    {
        _calls[method] = CallCount(method) + 1;

        if (Gate is not null)
        {
            await Gate.Task;
        }

        return result;
    }

    private static ApiCallResult<T> NotStubbed<T>() =>
        ApiCallResult<T>.Fail(ApiCallStatus.Unavailable, "This call has no canned response in FakeFoundryGateApiClient.");
}
