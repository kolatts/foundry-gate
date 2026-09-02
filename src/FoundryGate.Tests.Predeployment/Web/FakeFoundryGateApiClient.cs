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
/// no mocking library). Every method answers from a settable property, so a test overrides only the
/// call it cares about and inherits sensible successes for the rest; every mutation records what the
/// page sent, so a test can assert what was asked for as well as what was rendered.
/// </summary>
/// <remarks>
/// <see cref="Gate"/> holds every call until it is completed — that is how "submit is disabled while
/// the request is in flight" and "the dashboard is still loading" are observed, with no sleep
/// anywhere in the suite.
/// <para>
/// Declared <c>partial</c> on purpose: waves that add methods to <see cref="IFoundryGateApiClient"/>
/// (through its own <c>*.Admin.cs</c> / <c>*.Me.cs</c> partials) add the matching fakes in a partial
/// file of their own rather than editing this one, so two frontend waves never collide on it.
/// </para>
/// </remarks>
public sealed partial class FakeFoundryGateApiClient : IFoundryGateApiClient
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

    /// <summary>When set, every call awaits this before returning — release it to let the page finish.</summary>
    public TaskCompletionSource? Gate { get; set; }

    // -- Canned responses -----------------------------------------------------------------------

    public ApiCallResult<UserProfileResponse> MeResult { get; set; } = ApiCallResult<UserProfileResponse>.Ok(WebTestData.Profile());

    public ApiCallResult<PagedResult<UserResponse>> UsersResult { get; set; } =
        ApiCallResult<PagedResult<UserResponse>>.Ok(PagedResult<UserResponse>.Empty(1, 25));

    public ApiCallResult<UserResponse> UserResult { get; set; } = ApiCallResult<UserResponse>.Ok(WebTestData.User());

    public ApiCallResult<UserSyncResult> UserSyncResult { get; set; } =
        ApiCallResult<UserSyncResult>.Ok(new UserSyncResult(0, 0, 0, 0, 0));

    public ApiCallResult<PagedResult<GroupResponse>> GroupsResult { get; set; } =
        ApiCallResult<PagedResult<GroupResponse>>.Ok(PagedResult<GroupResponse>.Empty(1, 25));

    public ApiCallResult<GroupResponse> CreateGroupResult { get; set; } = ApiCallResult<GroupResponse>.Ok(WebTestData.Group());

    public ApiCallResult<GroupDetailResponse> GroupDetailResult { get; set; } =
        ApiCallResult<GroupDetailResponse>.Ok(new GroupDetailResponse(WebTestData.Group(), []));

    public ApiCallResult<PagedResult<GroupMemberResponse>> GroupMembersResult { get; set; } =
        ApiCallResult<PagedResult<GroupMemberResponse>>.Ok(PagedResult<GroupMemberResponse>.Empty(1, 25));

    public ApiCallResult<GroupSyncResult> GroupSyncResult { get; set; } =
        ApiCallResult<GroupSyncResult>.Ok(new GroupSyncResult(0, 0, 0, 0));

    public ApiCallResult<IReadOnlyList<FoundryModelResponse>> FoundryModelsResult { get; set; } =
        ApiCallResult<IReadOnlyList<FoundryModelResponse>>.Ok([WebTestData.Model()]);

    public ApiCallResult<IReadOnlyList<QuotaTierResponse>> QuotaTiersResult { get; set; } =
        ApiCallResult<IReadOnlyList<QuotaTierResponse>>.Ok(WebTestData.Tiers);

    public ApiCallResult<PagedResult<QuotaAllocationResponse>> QuotaAllocationsResult { get; set; } =
        ApiCallResult<PagedResult<QuotaAllocationResponse>>.Ok(PagedResult<QuotaAllocationResponse>.Empty(1, 25));

    public ApiCallResult<QuotaAllocationResponse> QuotaAllocationResult { get; set; } =
        ApiCallResult<QuotaAllocationResponse>.Ok(WebTestData.Allocation());

    public ApiCallResult<QuotaResetResult> QuotaResetResult { get; set; } =
        ApiCallResult<QuotaResetResult>.Ok(new QuotaResetResult(0, 2026, 9, DateTimeOffset.UnixEpoch));

    public ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>> RequestsResult { get; set; } =
        ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>.Ok(WebTestData.Page<QuotaIncreaseRequestResponse>());

    public ApiCallResult<QuotaIncreaseRequestResponse> RequestResult { get; set; } =
        ApiCallResult<QuotaIncreaseRequestResponse>.Ok(WebTestData.Request());

    public ApiCallResult<QuotaIncreaseRequestResponse> SubmitRequestResult { get; set; } =
        ApiCallResult<QuotaIncreaseRequestResponse>.Ok(WebTestData.Request());

    public ApiCallResult<ApiKeyResponse> KeyResult { get; set; } = ApiCallResult<ApiKeyResponse>.Ok(WebTestData.Key());

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

    /// <summary>What every "it worked" mutation returns — activate, deactivate, quota edits, membership changes.</summary>
    public ApiCallResult<bool> MutationResult { get; set; } = ApiCallResult<bool>.Ok(true);

    // -- Captured arguments ---------------------------------------------------------------------

    public List<SubmitQuotaIncreaseRequest> SubmittedRequests { get; } = [];

    public List<(string Key, string Value)> ConfigUpdates { get; } = [];

    public List<(AuditLogQuery Query, PagedRequest Paging)> AuditQueries { get; } = [];

    /// <summary>Every filtered <c>GET /requests</c> the pages made, in order.</summary>
    public List<(QuotaRequestQuery Query, PagedRequest Paging)> RequestListCalls { get; } = [];

    public List<(PagedRequest Paging, string? Search)> GroupListCalls { get; } = [];

    public List<int> ActivatedUserIds { get; } = [];

    public List<int> DeactivatedUserIds { get; } = [];

    public List<(int UserId, UpdateUserQuotaRequest Request)> QuotaUpdates { get; } = [];

    public List<CreateGroupRequest> CreatedGroups { get; } = [];

    public List<(int GroupId, UpdateGroupRequest Request)> GroupUpdates { get; } = [];

    public List<int> DeletedGroupIds { get; } = [];

    public List<(int GroupId, int UserId)> AddedGroupMembers { get; } = [];

    public List<(int GroupId, int UserId)> RemovedGroupMembers { get; } = [];

    public List<int> EntraSyncedGroupIds { get; } = [];

    public List<(int RequestId, ReviewQuotaIncreaseRequest Review)> ApprovedRequests { get; } = [];

    public List<(int RequestId, ReviewQuotaIncreaseRequest Review)> RejectedRequests { get; } = [];

    public List<int> ProvisionedKeyUserIds { get; } = [];

    public List<int> RotatedKeyUserIds { get; } = [];

    public List<int> RevokedKeyUserIds { get; } = [];

    /// <summary>How many times the named method was called (method names, e.g. <c>GetDashboardAsync</c>).</summary>
    public int CallCount(string method) => _calls.TryGetValue(method, out var count) ? count : 0;

    // -- Fluent arrange helpers -----------------------------------------------------------------

    public FakeFoundryGateApiClient ArrangeTiers(IReadOnlyList<QuotaTierResponse> tiers)
    {
        QuotaTiersResult = ApiCallResult<IReadOnlyList<QuotaTierResponse>>.Ok(tiers);
        return this;
    }

    public FakeFoundryGateApiClient ArrangeUsers(params UserResponse[] users)
    {
        UsersResult = ApiCallResult<PagedResult<UserResponse>>.Ok(WebTestData.Page(users));
        return this;
    }

    public FakeFoundryGateApiClient ArrangeGroups(params GroupResponse[] groups)
    {
        GroupsResult = ApiCallResult<PagedResult<GroupResponse>>.Ok(WebTestData.Page(groups));
        return this;
    }

    public FakeFoundryGateApiClient ArrangeGroup(GroupResponse group, params GroupMemberResponse[] members)
    {
        ArgumentNullException.ThrowIfNull(members);

        GroupDetailResult = ApiCallResult<GroupDetailResponse>.Ok(new GroupDetailResponse(group, members));
        GroupMembersResult = ApiCallResult<PagedResult<GroupMemberResponse>>.Ok(WebTestData.Page(members));
        return this;
    }

    public FakeFoundryGateApiClient ArrangeRequests(params QuotaIncreaseRequestResponse[] requests)
    {
        RequestsResult = ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>.Ok(WebTestData.Page(requests));
        return this;
    }

    public FakeFoundryGateApiClient ArrangeMutationFailure(ApiCallStatus status, string message)
    {
        MutationResult = ApiCallResult<bool>.Fail(status, message);
        return this;
    }

    // -- IFoundryGateApiClient ------------------------------------------------------------------

    public Task<ApiCallResult<UserProfileResponse>> GetMeAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetMeAsync), MeResult);

    public Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(PagedRequest paging, CancellationToken ct = default) =>
        RespondAsync(nameof(GetUsersAsync), UsersResult);

    public Task<ApiCallResult<UserResponse>> GetUserAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(GetUserAsync), UserResult);

    public Task<ApiCallResult<bool>> UpdateUserQuotaAsync(int userId, UpdateUserQuotaRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        QuotaUpdates.Add((userId, request));
        return RespondAsync(nameof(UpdateUserQuotaAsync), MutationResult);
    }

    public Task<ApiCallResult<bool>> ActivateUserAsync(int userId, CancellationToken ct = default)
    {
        ActivatedUserIds.Add(userId);
        return RespondAsync(nameof(ActivateUserAsync), MutationResult);
    }

    public Task<ApiCallResult<bool>> DeactivateUserAsync(int userId, CancellationToken ct = default)
    {
        DeactivatedUserIds.Add(userId);
        return RespondAsync(nameof(DeactivateUserAsync), MutationResult);
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

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, CancellationToken ct = default)
    {
        DeletedGroupIds.Add(groupId);
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
        RespondAsync(
            nameof(SyncGroupsFromEntraAsync),
            GroupSyncResult.IsSuccess && GroupSyncResult.Value is { } one
                ? ApiCallResult<IReadOnlyList<GroupSyncResult>>.Ok([one])
                : ApiCallResult<IReadOnlyList<GroupSyncResult>>.Fail(GroupSyncResult.Status, GroupSyncResult.Message ?? "Sync failed."));

    public Task<ApiCallResult<IReadOnlyList<FoundryModelResponse>>> GetFoundryModelsAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetFoundryModelsAsync), FoundryModelsResult);

    public Task<ApiCallResult<IReadOnlyList<QuotaTierResponse>>> GetQuotaTiersAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetQuotaTiersAsync), QuotaTiersResult);

    public Task<ApiCallResult<PagedResult<QuotaAllocationResponse>>> GetQuotaAllocationsAsync(PagedRequest paging, CancellationToken ct = default) =>
        RespondAsync(nameof(GetQuotaAllocationsAsync), QuotaAllocationsResult);

    public Task<ApiCallResult<QuotaAllocationResponse>> GetMyQuotaAllocationAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetMyQuotaAllocationAsync), QuotaAllocationResult);

    public Task<ApiCallResult<QuotaAllocationResponse>> GetUserQuotaAllocationAsync(int userId, CancellationToken ct = default) =>
        RespondAsync(nameof(GetUserQuotaAllocationAsync), QuotaAllocationResult);

    public Task<ApiCallResult<QuotaResetResult>> ResetQuotaAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(ResetQuotaAsync), QuotaResetResult);

    public Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(PagedRequest paging, CancellationToken ct = default) =>
        RespondAsync(nameof(GetRequestsAsync), RequestsResult);

    public Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(
        QuotaRequestQuery query,
        PagedRequest paging,
        CancellationToken ct = default)
    {
        RequestListCalls.Add((query, paging));
        return RespondAsync(nameof(GetRequestsAsync), RequestsResult);
    }

    public Task<ApiCallResult<QuotaIncreaseRequestResponse>> SubmitRequestAsync(SubmitQuotaIncreaseRequest request, CancellationToken ct = default)
    {
        SubmittedRequests.Add(request);
        return RespondAsync(nameof(SubmitRequestAsync), SubmitRequestResult);
    }

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
        RespondAsync(nameof(GetMyKeyAsync), KeyResult);

    public Task<ApiCallResult<ApiKeyRevealResponse>> RevealMyKeyAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(RevealMyKeyAsync), RevealKeyResult);

    public Task<ApiCallResult<ApiKeyRevealResponse>> RotateMyKeyAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(RotateMyKeyAsync), RotateKeyResult);

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

    public Task<ApiCallResult<DashboardSummaryResponse>> GetDashboardAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetDashboardAsync), DashboardResult);

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
}
