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

    // -- Canned responses for the admin management pages (#51-#53, #62, #63) --------------------

    public ApiCallResult<PagedResult<UserResponse>> UsersResult { get; set; } =
        ApiCallResult<PagedResult<UserResponse>>.Ok(WebTestData.Page<UserResponse>());

    public ApiCallResult<UserDetailResponse> UserDetailResult { get; set; } =
        ApiCallResult<UserDetailResponse>.Fail(ApiCallStatus.NotFound, "No user arranged for this test.");

    public ApiCallResult<UserSyncResult> UserSyncResult { get; set; } =
        ApiCallResult<UserSyncResult>.Ok(new UserSyncResult(0, 0, 0, 0, 0));

    public ApiCallResult<PagedResult<GroupResponse>> GroupsResult { get; set; } =
        ApiCallResult<PagedResult<GroupResponse>>.Ok(WebTestData.Page<GroupResponse>());

    public ApiCallResult<GroupDetailResponse> GroupDetailResult { get; set; } =
        ApiCallResult<GroupDetailResponse>.Fail(ApiCallStatus.NotFound, "No group arranged for this test.");

    public ApiCallResult<PagedResult<GroupMemberResponse>> GroupMembersResult { get; set; } =
        ApiCallResult<PagedResult<GroupMemberResponse>>.Ok(WebTestData.Page<GroupMemberResponse>());

    public ApiCallResult<GroupResponse> CreateGroupResult { get; set; } =
        ApiCallResult<GroupResponse>.Fail(ApiCallStatus.Error, "No create result arranged for this test.");

    public ApiCallResult<GroupSyncResult> GroupSyncResult { get; set; } =
        ApiCallResult<GroupSyncResult>.Ok(new GroupSyncResult(0, 0, 0, 0));

    public ApiCallResult<QuotaIncreaseRequestResponse> RequestResult { get; set; } =
        ApiCallResult<QuotaIncreaseRequestResponse>.Fail(ApiCallStatus.NotFound, "No request arranged for this test.");

    public ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>> FoundryDeploymentsResult { get; set; } =
        ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>.Ok([]);

    public ApiCallResult<FoundryDeploymentResponse> CreateFoundryDeploymentResult { get; set; } =
        ApiCallResult<FoundryDeploymentResponse>.Fail(ApiCallStatus.Error, "No create result arranged for this test.");

    /// <summary>What the user row-returning mutations (quota, activate, deactivate) answer.</summary>
    public ApiCallResult<UserResponse> UserMutationResult { get; set; } =
        ApiCallResult<UserResponse>.Ok(AdminTestData.User());

    /// <summary>What every "it worked" mutation answers. Set a failure to exercise the error path.</summary>
    public ApiCallResult<bool> MutationResult { get; set; } = ApiCallResult<bool>.Ok(true);

    // -- Captured arguments ---------------------------------------------------------------------

    public List<SubmitQuotaIncreaseRequest> SubmittedRequests { get; } = [];

    public List<(string Key, string Value)> ConfigUpdates { get; } = [];

    public List<(AuditLogQuery Query, PagedRequest Paging)> AuditQueries { get; } = [];

    public List<(QuotaRequestQuery Query, PagedRequest Paging)> FilteredRequestQueries { get; } = [];

    public List<(UserListQuery Query, PagedRequest Paging)> UserListCalls { get; } = [];

    public List<int> ActivatedUserIds { get; } = [];

    public List<int> DeactivatedUserIds { get; } = [];

    public List<(int UserId, UpdateUserQuotaRequest Request)> QuotaUpdates { get; } = [];

    public List<(PagedRequest Paging, string? Search)> GroupListCalls { get; } = [];

    public List<CreateGroupRequest> CreatedGroups { get; } = [];

    public List<(int GroupId, UpdateGroupRequest Request)> GroupUpdates { get; } = [];

    public List<(int GroupId, bool Force)> DeletedGroups { get; } = [];

    public List<(int GroupId, int UserId)> AddedGroupMembers { get; } = [];

    public List<(int GroupId, int UserId)> RemovedGroupMembers { get; } = [];

    public List<int> EntraSyncedGroupIds { get; } = [];

    public List<(int RequestId, ReviewQuotaIncreaseRequest Review)> ApprovedRequests { get; } = [];

    public List<(int RequestId, ReviewQuotaIncreaseRequest Review)> RejectedRequests { get; } = [];

    public List<int> ProvisionedKeyUserIds { get; } = [];

    public List<int> RotatedKeyUserIds { get; } = [];

    public List<int> RevokedKeyUserIds { get; } = [];

    public List<CreateFoundryDeploymentRequest> CreatedDeployments { get; } = [];

    public List<(string AccountName, string DeploymentName)> DeletedDeployments { get; } = [];

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

    // -- Admin management pages (#51-#53, #62, #63) ----------------------------------------------

    public Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(PagedRequest paging, CancellationToken ct = default) =>
        GetUsersAsync(new UserListQuery(null, null), paging, ct);

    public Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(UserListQuery query, PagedRequest paging, CancellationToken ct = default)
    {
        UserListCalls.Add((query, paging));
        return RespondAsync(nameof(GetUsersAsync), UsersResult);
    }

    public Task<ApiCallResult<UserDetailResponse>> GetUserAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(GetUserAsync), UserDetailResult);

    public Task<ApiCallResult<UserResponse>> UpdateUserQuotaAsync(int userId, UpdateUserQuotaRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        QuotaUpdates.Add((userId, request));
        return RespondAsync(nameof(UpdateUserQuotaAsync), UserMutationResult);
    }

    public Task<ApiCallResult<UserResponse>> ActivateUserAsync(int userId, CancellationToken ct = default)
    {
        ActivatedUserIds.Add(userId);
        return RespondAsync(nameof(ActivateUserAsync), UserMutationResult);
    }

    public Task<ApiCallResult<UserResponse>> DeactivateUserAsync(int userId, CancellationToken ct = default)
    {
        DeactivatedUserIds.Add(userId);
        return RespondAsync(nameof(DeactivateUserAsync), UserMutationResult);
    }

    public Task<ApiCallResult<UserSyncResult>> SyncUsersAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(SyncUsersAsync), UserSyncResult);

    public Task<ApiCallResult<PagedResult<GroupResponse>>> GetGroupsAsync(PagedRequest paging, string? search = null, CancellationToken ct = default)
    {
        GroupListCalls.Add((paging, search));
        return RespondAsync(nameof(GetGroupsAsync), GroupsResult);
    }

    public Task<ApiCallResult<GroupResponse>> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        CreatedGroups.Add(request);
        return RespondAsync(nameof(CreateGroupAsync), CreateGroupResult);
    }

    public Task<ApiCallResult<GroupDetailResponse>> GetGroupAsync(int groupId, CancellationToken ct = default) =>
        RespondAsync(nameof(GetGroupAsync), GroupDetailResult);

    public Task<ApiCallResult<bool>> UpdateGroupAsync(int groupId, UpdateGroupRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        GroupUpdates.Add((groupId, request));
        return RespondAsync(nameof(UpdateGroupAsync), MutationResult);
    }

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, CancellationToken ct = default) =>
        DeleteGroupAsync(groupId, force: false, ct);

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, bool force, CancellationToken ct = default)
    {
        DeletedGroups.Add((groupId, force));
        return RespondAsync(nameof(DeleteGroupAsync), MutationResult);
    }

    public Task<ApiCallResult<bool>> AddGroupMemberAsync(int groupId, AddGroupMemberRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        AddedGroupMembers.Add((groupId, request.UserId));
        return RespondAsync(nameof(AddGroupMemberAsync), MutationResult);
    }

    public Task<ApiCallResult<bool>> RemoveGroupMemberAsync(int groupId, int userId, CancellationToken ct = default)
    {
        RemovedGroupMembers.Add((groupId, userId));
        return RespondAsync(nameof(RemoveGroupMemberAsync), MutationResult);
    }

    public Task<ApiCallResult<PagedResult<GroupMemberResponse>>> GetGroupMembersAsync(int groupId, PagedRequest paging, CancellationToken ct = default) =>
        RespondAsync(nameof(GetGroupMembersAsync), GroupMembersResult);

    public Task<ApiCallResult<GroupSyncResult>> SyncGroupFromEntraAsync(int groupId, CancellationToken ct = default)
    {
        EntraSyncedGroupIds.Add(groupId);
        return RespondAsync(nameof(SyncGroupFromEntraAsync), GroupSyncResult);
    }

    public Task<ApiCallResult<IReadOnlyList<GroupSyncResult>>> SyncGroupsFromEntraAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(SyncGroupsFromEntraAsync), ApiCallResult<IReadOnlyList<GroupSyncResult>>.Ok([]));

    public Task<ApiCallResult<PagedResult<QuotaAllocationResponse>>> GetQuotaAllocationsAsync(PagedRequest paging, CancellationToken ct = default) =>
        RespondAsync(nameof(GetQuotaAllocationsAsync), ApiCallResult<PagedResult<QuotaAllocationResponse>>.Ok(WebTestData.Page<QuotaAllocationResponse>()));

    public Task<ApiCallResult<QuotaAllocationResponse>> GetMyQuotaAllocationAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetMyQuotaAllocationAsync), ApiCallResult<QuotaAllocationResponse>.Ok(WebTestData.Allocation()));

    public Task<ApiCallResult<QuotaAllocationResponse>> GetUserQuotaAllocationAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(
            nameof(GetUserQuotaAllocationAsync),
            UserDetailResult.Value?.CurrentAllocation is { } allocation
                ? ApiCallResult<QuotaAllocationResponse>.Ok(allocation)
                : NotStubbed<QuotaAllocationResponse>());

    public Task<ApiCallResult<QuotaResetResult>> ResetQuotaAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(ResetQuotaAsync), NotStubbed<QuotaResetResult>());

    public Task<ApiCallResult<QuotaIncreaseRequestResponse>> GetRequestAsync(int requestId, CancellationToken ct = default) =>
        RespondAsync(nameof(GetRequestAsync), RequestResult);

    public Task<ApiCallResult<bool>> ApproveRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApprovedRequests.Add((requestId, request));
        return RespondAsync(nameof(ApproveRequestAsync), MutationResult);
    }

    public Task<ApiCallResult<bool>> RejectRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RejectedRequests.Add((requestId, request));
        return RespondAsync(nameof(RejectRequestAsync), MutationResult);
    }

    public Task<ApiCallResult<ApiKeyResponse>> GetMyKeyAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetMyKeyAsync), ApiCallResult<ApiKeyResponse>.Ok(WebTestData.Key()));

    public Task<ApiCallResult<ApiKeyRevealResponse>> RotateUserKeyAsync(int userId, CancellationToken ct = default)
    {
        RotatedKeyUserIds.Add(userId);
        return RespondAsync(nameof(RotateUserKeyAsync), RotateKeyResult);
    }

    public Task<ApiCallResult<ApiKeyRevealResponse>> ProvisionUserKeyAsync(int userId, CancellationToken ct = default)
    {
        ProvisionedKeyUserIds.Add(userId);
        return RespondAsync(nameof(ProvisionUserKeyAsync), RevealKeyResult);
    }

    public Task<ApiCallResult<bool>> RevokeUserKeyAsync(int userId, CancellationToken ct = default)
    {
        RevokedKeyUserIds.Add(userId);
        return RespondAsync(nameof(RevokeUserKeyAsync), MutationResult);
    }

    public Task<ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>> GetFoundryDeploymentsAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetFoundryDeploymentsAsync), FoundryDeploymentsResult);

    public Task<ApiCallResult<FoundryDeploymentResponse>> CreateFoundryDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        CreatedDeployments.Add(request);
        return RespondAsync(nameof(CreateFoundryDeploymentAsync), CreateFoundryDeploymentResult);
    }

    public Task<ApiCallResult<bool>> DeleteFoundryDeploymentAsync(string accountName, string deploymentName, CancellationToken ct = default)
    {
        DeletedDeployments.Add((accountName, deploymentName));
        return RespondAsync(nameof(DeleteFoundryDeploymentAsync), MutationResult);
    }

    // -- Arrange helpers ------------------------------------------------------------------------

    /// <summary>The tier catalogue every quota display and editor reads.</summary>
    public FakeFoundryGateApiClient ArrangeTiers(IReadOnlyList<QuotaTierResponse> tiers)
    {
        QuotaTiersResult = ApiCallResult<IReadOnlyList<QuotaTierResponse>>.Ok(tiers);
        return this;
    }

    /// <summary>One full page of users for <c>GET /users</c>.</summary>
    public FakeFoundryGateApiClient ArrangeUsers(params UserResponse[] users)
    {
        UsersResult = ApiCallResult<PagedResult<UserResponse>>.Ok(WebTestData.Page(users));
        return this;
    }

    /// <summary>One full page of groups for <c>GET /groups</c>.</summary>
    public FakeFoundryGateApiClient ArrangeGroups(params GroupResponse[] groups)
    {
        GroupsResult = ApiCallResult<PagedResult<GroupResponse>>.Ok(WebTestData.Page(groups));
        return this;
    }

    /// <summary>A group plus its roster: <c>GET /groups/{id}</c> and <c>GET /groups/{id}/members</c> together.</summary>
    public FakeFoundryGateApiClient ArrangeGroup(GroupResponse group, params GroupMemberResponse[] members)
    {
        GroupDetailResult = ApiCallResult<GroupDetailResponse>.Ok(new GroupDetailResponse(group, members));
        GroupMembersResult = ApiCallResult<PagedResult<GroupMemberResponse>>.Ok(WebTestData.Page(members));
        return this;
    }

    /// <summary>One full page of quota-increase requests for <c>GET /requests</c>.</summary>
    public FakeFoundryGateApiClient ArrangeRequests(params QuotaIncreaseRequestResponse[] requests)
    {
        RequestsResult = ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>.Ok(WebTestData.Page(requests));
        return this;
    }

    /// <summary>What <c>GET /foundry/deployments</c> returns.</summary>
    public FakeFoundryGateApiClient ArrangeDeployments(params FoundryDeploymentResponse[] deployments)
    {
        FoundryDeploymentsResult = ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>.Ok(deployments);
        return this;
    }

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
