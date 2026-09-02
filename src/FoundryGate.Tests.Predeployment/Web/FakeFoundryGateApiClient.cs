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
/// In-memory <see cref="IFoundryGateApiClient"/> for the component tests. Every read has a
/// settable canned result and every write records what it was asked to do, so a test arranges
/// the page's data up front and then asserts on which calls the UI actually made — the thing
/// that matters for "does this dialog gate the mutation".
/// </summary>
/// <remarks>
/// Hand-rolled rather than mocked: the repo has no mocking library (CONVENTIONS.md §Testing),
/// and a fake with named recording lists reads better in the assertions than set-up expectations
/// anyway. Unset results default to empty-and-successful, so a test only arranges what it cares
/// about.
/// </remarks>
public sealed class FakeFoundryGateApiClient : IFoundryGateApiClient
{
    // ── Canned results ─────────────────────────────────────────────────────────

    public ApiCallResult<PagedResult<UserResponse>> UsersResult { get; set; } = Ok(PagedResult<UserResponse>.Empty(1, 25));

    public ApiCallResult<UserDetailResponse> UserDetailResult { get; set; } =
        ApiCallResult<UserDetailResponse>.Fail(ApiCallStatus.NotFound, "No user arranged for this test.");

    public ApiCallResult<UserProfileResponse> MeResult { get; set; } =
        ApiCallResult<UserProfileResponse>.Fail(ApiCallStatus.NotFound, "No profile arranged for this test.");

    public ApiCallResult<UserSyncResult> UserSyncResult { get; set; } = Ok(new UserSyncResult(0, 0, 0, 0, 0));

    public ApiCallResult<PagedResult<GroupResponse>> GroupsResult { get; set; } = Ok(PagedResult<GroupResponse>.Empty(1, 25));

    public ApiCallResult<GroupDetailResponse> GroupDetailResult { get; set; } =
        ApiCallResult<GroupDetailResponse>.Fail(ApiCallStatus.NotFound, "No group arranged for this test.");

    public ApiCallResult<PagedResult<GroupMemberResponse>> GroupMembersResult { get; set; } = Ok(PagedResult<GroupMemberResponse>.Empty(1, 25));

    public ApiCallResult<GroupResponse> CreateGroupResult { get; set; } =
        ApiCallResult<GroupResponse>.Fail(ApiCallStatus.Error, "No create result arranged for this test.");

    public ApiCallResult<GroupSyncResult> GroupSyncResult { get; set; } = Ok(new GroupSyncResult(0, 0, 0, 0));

    public ApiCallResult<IReadOnlyList<QuotaTierResponse>> QuotaTiersResult { get; set; } = Ok<IReadOnlyList<QuotaTierResponse>>([]);

    public ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>> RequestsResult { get; set; } =
        Ok(PagedResult<QuotaIncreaseRequestResponse>.Empty(1, 25));

    public ApiCallResult<QuotaIncreaseRequestResponse> RequestResult { get; set; } =
        ApiCallResult<QuotaIncreaseRequestResponse>.Fail(ApiCallStatus.NotFound, "No request arranged for this test.");

    public ApiCallResult<ApiKeyRevealResponse> RevealResult { get; set; } =
        Ok(new ApiKeyRevealResponse("fg-plaintext-key", "fg-…-key", "sub-1", DateTimeOffset.UnixEpoch));

    public ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>> FoundryDeploymentsResult { get; set; } =
        Ok<IReadOnlyList<FoundryDeploymentResponse>>([]);

    public ApiCallResult<FoundryDeploymentResponse> CreateFoundryDeploymentResult { get; set; } =
        ApiCallResult<FoundryDeploymentResponse>.Fail(ApiCallStatus.Error, "No create result arranged for this test.");

    /// <summary>What every "it worked" mutation answers. Set to a failure to test the error path.</summary>
    public ApiCallResult<bool> MutationResult { get; set; } = ApiCallResult<bool>.Ok(true);

    // ── Recorded calls ─────────────────────────────────────────────────────────

    public List<(UserListQuery Query, PagedRequest Paging)> UserListCalls { get; } = [];

    public List<int> ActivatedUserIds { get; } = [];

    public List<int> DeactivatedUserIds { get; } = [];

    public List<(int UserId, UpdateUserQuotaRequest Request)> QuotaUpdates { get; } = [];

    public int UserSyncCallCount { get; private set; }

    public List<(PagedRequest Paging, string? Search)> GroupListCalls { get; } = [];

    public List<CreateGroupRequest> CreatedGroups { get; } = [];

    public List<(int GroupId, UpdateGroupRequest Request)> GroupUpdates { get; } = [];

    public List<(int GroupId, bool Force)> DeletedGroups { get; } = [];

    public List<(int GroupId, int UserId)> AddedGroupMembers { get; } = [];

    public List<(int GroupId, int UserId)> RemovedGroupMembers { get; } = [];

    public List<int> EntraSyncedGroupIds { get; } = [];

    public List<(QuotaRequestQuery Query, PagedRequest Paging)> RequestListCalls { get; } = [];

    public List<(int RequestId, ReviewQuotaIncreaseRequest Review)> ApprovedRequests { get; } = [];

    public List<(int RequestId, ReviewQuotaIncreaseRequest Review)> RejectedRequests { get; } = [];

    public List<int> ProvisionedKeyUserIds { get; } = [];

    public List<int> RotatedKeyUserIds { get; } = [];

    public List<int> RevokedKeyUserIds { get; } = [];

    public List<CreateFoundryDeploymentRequest> CreatedDeployments { get; } = [];

    public List<(string AccountName, string DeploymentName)> DeletedDeployments { get; } = [];

    // ── Arrange helpers ────────────────────────────────────────────────────────

    /// <summary>The tier catalogue every quota display and editor reads.</summary>
    public FakeFoundryGateApiClient ArrangeTiers(IReadOnlyList<QuotaTierResponse> tiers)
    {
        QuotaTiersResult = Ok(tiers);
        return this;
    }

    /// <summary>One full page of users for <c>GET /users</c>.</summary>
    public FakeFoundryGateApiClient ArrangeUsers(params UserResponse[] users)
    {
        ArgumentNullException.ThrowIfNull(users);
        UsersResult = Ok(new PagedResult<UserResponse>(users, users.Length, 1, 25));
        return this;
    }

    /// <summary>One full page of groups for <c>GET /groups</c>.</summary>
    public FakeFoundryGateApiClient ArrangeGroups(params GroupResponse[] groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        GroupsResult = Ok(new PagedResult<GroupResponse>(groups, groups.Length, 1, 25));
        return this;
    }

    /// <summary>A group plus its roster: <c>GET /groups/{id}</c> and <c>GET /groups/{id}/members</c> together.</summary>
    public FakeFoundryGateApiClient ArrangeGroup(GroupResponse group, params GroupMemberResponse[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        GroupDetailResult = Ok(new GroupDetailResponse(group, members));
        GroupMembersResult = Ok(new PagedResult<GroupMemberResponse>(members, members.Length, 1, 25));
        return this;
    }

    /// <summary>One full page of quota-increase requests for <c>GET /requests</c>.</summary>
    public FakeFoundryGateApiClient ArrangeRequests(params QuotaIncreaseRequestResponse[] requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        RequestsResult = Ok(new PagedResult<QuotaIncreaseRequestResponse>(requests, requests.Length, 1, 25));
        return this;
    }

    /// <summary>What <c>GET /foundry/deployments</c> returns.</summary>
    public FakeFoundryGateApiClient ArrangeDeployments(params FoundryDeploymentResponse[] deployments)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        FoundryDeploymentsResult = Ok<IReadOnlyList<FoundryDeploymentResponse>>(deployments);
        return this;
    }

    /// <summary>Makes every "it worked" mutation fail, so the error path can be exercised.</summary>
    public FakeFoundryGateApiClient ArrangeMutationFailure(ApiCallStatus status, string message)
    {
        MutationResult = ApiCallResult<bool>.Fail(status, message);
        return this;
    }

    // ── Users ──────────────────────────────────────────────────────────────────

    public Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(PagedRequest paging, CancellationToken ct = default) =>
        GetUsersAsync(new UserListQuery(null, null), paging, ct);

    public Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(UserListQuery query, PagedRequest paging, CancellationToken ct = default)
    {
        UserListCalls.Add((query, paging));
        return Task.FromResult(UsersResult);
    }

    public Task<ApiCallResult<UserProfileResponse>> GetMeAsync(CancellationToken ct = default) => Task.FromResult(MeResult);

    public Task<ApiCallResult<UserResponse>> GetUserAsync(int userId, CancellationToken ct = default) =>
        Task.FromResult(UserDetailResult.Value is { } detail
            ? ApiCallResult<UserResponse>.Ok(detail.User)
            : ApiCallResult<UserResponse>.Fail(UserDetailResult.Status, UserDetailResult.Message ?? "Not arranged."));

    public Task<ApiCallResult<UserDetailResponse>> GetUserDetailAsync(int userId, CancellationToken ct = default) =>
        Task.FromResult(UserDetailResult);

    public Task<ApiCallResult<bool>> UpdateUserQuotaAsync(int userId, UpdateUserQuotaRequest request, CancellationToken ct = default)
    {
        QuotaUpdates.Add((userId, request));
        return Task.FromResult(MutationResult);
    }

    public Task<ApiCallResult<bool>> ActivateUserAsync(int userId, CancellationToken ct = default)
    {
        ActivatedUserIds.Add(userId);
        return Task.FromResult(MutationResult);
    }

    public Task<ApiCallResult<bool>> DeactivateUserAsync(int userId, CancellationToken ct = default)
    {
        DeactivatedUserIds.Add(userId);
        return Task.FromResult(MutationResult);
    }

    public Task<ApiCallResult<UserSyncResult>> SyncUsersAsync(CancellationToken ct = default)
    {
        UserSyncCallCount++;
        return Task.FromResult(UserSyncResult);
    }

    // ── Groups ─────────────────────────────────────────────────────────────────

    public Task<ApiCallResult<PagedResult<GroupResponse>>> GetGroupsAsync(PagedRequest paging, string? search = null, CancellationToken ct = default)
    {
        GroupListCalls.Add((paging, search));
        return Task.FromResult(GroupsResult);
    }

    public Task<ApiCallResult<GroupResponse>> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default)
    {
        CreatedGroups.Add(request);
        return Task.FromResult(CreateGroupResult);
    }

    public Task<ApiCallResult<GroupDetailResponse>> GetGroupAsync(int groupId, CancellationToken ct = default) =>
        Task.FromResult(GroupDetailResult);

    public Task<ApiCallResult<bool>> UpdateGroupAsync(int groupId, UpdateGroupRequest request, CancellationToken ct = default)
    {
        GroupUpdates.Add((groupId, request));
        return Task.FromResult(MutationResult);
    }

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, CancellationToken ct = default) =>
        DeleteGroupAsync(groupId, force: false, ct);

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, bool force, CancellationToken ct = default)
    {
        DeletedGroups.Add((groupId, force));
        return Task.FromResult(MutationResult);
    }

    public Task<ApiCallResult<bool>> AddGroupMemberAsync(int groupId, AddGroupMemberRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AddedGroupMembers.Add((groupId, request.UserId));
        return Task.FromResult(MutationResult);
    }

    public Task<ApiCallResult<bool>> RemoveGroupMemberAsync(int groupId, int userId, CancellationToken ct = default)
    {
        RemovedGroupMembers.Add((groupId, userId));
        return Task.FromResult(MutationResult);
    }

    public Task<ApiCallResult<PagedResult<GroupMemberResponse>>> GetGroupMembersAsync(int groupId, PagedRequest paging, CancellationToken ct = default) =>
        Task.FromResult(GroupMembersResult);

    public Task<ApiCallResult<GroupSyncResult>> SyncGroupFromEntraAsync(int groupId, CancellationToken ct = default)
    {
        EntraSyncedGroupIds.Add(groupId);
        return Task.FromResult(GroupSyncResult);
    }

    public Task<ApiCallResult<IReadOnlyList<GroupSyncResult>>> SyncGroupsFromEntraAsync(CancellationToken ct = default) =>
        Task.FromResult(Ok<IReadOnlyList<GroupSyncResult>>([]));

    // ── Quota ──────────────────────────────────────────────────────────────────

    public Task<ApiCallResult<IReadOnlyList<QuotaTierResponse>>> GetQuotaTiersAsync(CancellationToken ct = default) =>
        Task.FromResult(QuotaTiersResult);

    public Task<ApiCallResult<PagedResult<QuotaAllocationResponse>>> GetQuotaAllocationsAsync(PagedRequest paging, CancellationToken ct = default) =>
        Task.FromResult(Ok(PagedResult<QuotaAllocationResponse>.Empty(1, 25)));

    public Task<ApiCallResult<QuotaAllocationResponse>> GetMyQuotaAllocationAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiCallResult<QuotaAllocationResponse>.Fail(ApiCallStatus.NotFound, "No allocation arranged for this test."));

    public Task<ApiCallResult<QuotaAllocationResponse>> GetUserQuotaAllocationAsync(int userId, CancellationToken ct = default) =>
        Task.FromResult(UserDetailResult.Value?.CurrentAllocation is { } allocation
            ? ApiCallResult<QuotaAllocationResponse>.Ok(allocation)
            : ApiCallResult<QuotaAllocationResponse>.Fail(ApiCallStatus.NotFound, "No allocation arranged for this test."));

    public Task<ApiCallResult<QuotaResetResult>> ResetQuotaAsync(CancellationToken ct = default) =>
        Task.FromResult(Ok(new QuotaResetResult(0, 2026, 9, DateTimeOffset.UnixEpoch)));

    // ── Quota increase requests ────────────────────────────────────────────────

    public Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(PagedRequest paging, CancellationToken ct = default) =>
        GetRequestsAsync(new QuotaRequestQuery(null, null), paging, ct);

    public Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(QuotaRequestQuery query, PagedRequest paging, CancellationToken ct = default)
    {
        RequestListCalls.Add((query, paging));
        return Task.FromResult(RequestsResult);
    }

    public Task<ApiCallResult<QuotaIncreaseRequestResponse>> SubmitRequestAsync(SubmitQuotaIncreaseRequest request, CancellationToken ct = default) =>
        Task.FromResult(RequestResult);

    public Task<ApiCallResult<QuotaIncreaseRequestResponse>> GetRequestAsync(int requestId, CancellationToken ct = default) =>
        Task.FromResult(RequestResult);

    public Task<ApiCallResult<bool>> ApproveRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default)
    {
        ApprovedRequests.Add((requestId, request));
        return Task.FromResult(MutationResult);
    }

    public Task<ApiCallResult<bool>> RejectRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default)
    {
        RejectedRequests.Add((requestId, request));
        return Task.FromResult(MutationResult);
    }

    // ── Keys ───────────────────────────────────────────────────────────────────

    public Task<ApiCallResult<ApiKeyResponse>> GetMyKeyAsync(CancellationToken ct = default) =>
        Task.FromResult(Ok(new ApiKeyResponse(false, null, null)));

    public Task<ApiCallResult<ApiKeyRevealResponse>> RotateMyKeyAsync(CancellationToken ct = default) => Task.FromResult(RevealResult);

    public Task<ApiCallResult<ApiKeyRevealResponse>> RotateUserKeyAsync(int userId, CancellationToken ct = default)
    {
        RotatedKeyUserIds.Add(userId);
        return Task.FromResult(RevealResult);
    }

    public Task<ApiCallResult<ApiKeyRevealResponse>> ProvisionUserKeyAsync(int userId, CancellationToken ct = default)
    {
        ProvisionedKeyUserIds.Add(userId);
        return Task.FromResult(RevealResult);
    }

    public Task<ApiCallResult<bool>> RevokeUserKeyAsync(int userId, CancellationToken ct = default)
    {
        RevokedKeyUserIds.Add(userId);
        return Task.FromResult(MutationResult);
    }

    // ── Foundry ────────────────────────────────────────────────────────────────

    public Task<ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>> GetFoundryDeploymentsAsync(CancellationToken ct = default) =>
        Task.FromResult(FoundryDeploymentsResult);

    public Task<ApiCallResult<FoundryDeploymentResponse>> CreateFoundryDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken ct = default)
    {
        CreatedDeployments.Add(request);
        return Task.FromResult(CreateFoundryDeploymentResult);
    }

    public Task<ApiCallResult<bool>> DeleteFoundryDeploymentAsync(string accountName, string deploymentName, CancellationToken ct = default)
    {
        DeletedDeployments.Add((accountName, deploymentName));
        return Task.FromResult(MutationResult);
    }

    // ── Admin / configuration ──────────────────────────────────────────────────

    public Task<ApiCallResult<IReadOnlyList<SystemConfigEntryResponse>>> GetConfigAsync(CancellationToken ct = default) =>
        Task.FromResult(Ok<IReadOnlyList<SystemConfigEntryResponse>>([]));

    public Task<ApiCallResult<bool>> UpdateConfigAsync(string key, UpdateSystemConfigRequest request, CancellationToken ct = default) =>
        Task.FromResult(MutationResult);

    public Task<ApiCallResult<PagedResult<AuditLogEntryResponse>>> GetAuditLogAsync(AuditLogQuery query, PagedRequest paging, CancellationToken ct = default) =>
        Task.FromResult(Ok(PagedResult<AuditLogEntryResponse>.Empty(1, 25)));

    public Task<ApiCallResult<DashboardSummaryResponse>> GetDashboardAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiCallResult<DashboardSummaryResponse>.Fail(ApiCallStatus.NotFound, "No dashboard arranged for this test."));

    private static ApiCallResult<T> Ok<T>(T value) => ApiCallResult<T>.Ok(value);
}
