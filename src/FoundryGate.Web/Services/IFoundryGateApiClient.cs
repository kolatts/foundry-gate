using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Dashboard.Contracts;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Web.Services;

/// <summary>
/// Typed client over FoundryGate.Api's <c>/api/v1</c> surface (foundrygate-spec.md §4).
/// Every method returns an <see cref="ApiCallResult{T}"/> instead of throwing — the API
/// doesn't exist yet at the time this shell ships (#48), so every call site (starting
/// with the home page's smoke call) must be able to render "signed out", "not found",
/// and "API is down" without crashing. No method here does any UI-facing formatting or
/// caching; that belongs to the feature epics that consume this client.
/// </summary>
public partial interface IFoundryGateApiClient
{
    // Users — spec §4.1
    Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(PagedRequest paging, CancellationToken ct = default);

    Task<ApiCallResult<UserProfileResponse>> GetMeAsync(CancellationToken ct = default);

    Task<ApiCallResult<UserResponse>> GetUserAsync(int userId, CancellationToken ct = default);

    Task<ApiCallResult<bool>> UpdateUserQuotaAsync(int userId, UpdateUserQuotaRequest request, CancellationToken ct = default);

    Task<ApiCallResult<bool>> ActivateUserAsync(int userId, CancellationToken ct = default);

    Task<ApiCallResult<bool>> DeactivateUserAsync(int userId, CancellationToken ct = default);

    Task<ApiCallResult<UserSyncResult>> SyncUsersAsync(CancellationToken ct = default);

    // Groups — spec §4.2
    Task<ApiCallResult<PagedResult<GroupResponse>>> GetGroupsAsync(PagedRequest paging, string? search = null, CancellationToken ct = default);

    Task<ApiCallResult<GroupResponse>> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default);

    Task<ApiCallResult<GroupDetailResponse>> GetGroupAsync(int groupId, CancellationToken ct = default);

    Task<ApiCallResult<bool>> UpdateGroupAsync(int groupId, UpdateGroupRequest request, CancellationToken ct = default);

    Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, CancellationToken ct = default);

    Task<ApiCallResult<bool>> AddGroupMemberAsync(int groupId, AddGroupMemberRequest request, CancellationToken ct = default);

    Task<ApiCallResult<bool>> RemoveGroupMemberAsync(int groupId, int userId, CancellationToken ct = default);

    Task<ApiCallResult<PagedResult<GroupMemberResponse>>> GetGroupMembersAsync(int groupId, PagedRequest paging, CancellationToken ct = default);

    Task<ApiCallResult<GroupSyncResult>> SyncGroupFromEntraAsync(int groupId, CancellationToken ct = default);

    Task<ApiCallResult<IReadOnlyList<GroupSyncResult>>> SyncGroupsFromEntraAsync(CancellationToken ct = default);

    // Quota — spec §4.3
    Task<ApiCallResult<PagedResult<QuotaAllocationResponse>>> GetQuotaAllocationsAsync(PagedRequest paging, CancellationToken ct = default);

    Task<ApiCallResult<QuotaAllocationResponse>> GetMyQuotaAllocationAsync(CancellationToken ct = default);

    Task<ApiCallResult<QuotaAllocationResponse>> GetUserQuotaAllocationAsync(int userId, CancellationToken ct = default);

    Task<ApiCallResult<QuotaResetResult>> ResetQuotaAsync(CancellationToken ct = default);

    // Quota increase requests — spec §4.4
    Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(PagedRequest paging, CancellationToken ct = default);

    Task<ApiCallResult<QuotaIncreaseRequestResponse>> SubmitRequestAsync(SubmitQuotaIncreaseRequest request, CancellationToken ct = default);

    Task<ApiCallResult<QuotaIncreaseRequestResponse>> GetRequestAsync(int requestId, CancellationToken ct = default);

    Task<ApiCallResult<bool>> ApproveRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default);

    Task<ApiCallResult<bool>> RejectRequestAsync(int requestId, ReviewQuotaIncreaseRequest request, CancellationToken ct = default);

    // Keys — spec §4.5
    Task<ApiCallResult<ApiKeyResponse>> GetMyKeyAsync(CancellationToken ct = default);

    Task<ApiCallResult<ApiKeyRevealResponse>> RotateMyKeyAsync(CancellationToken ct = default);

    Task<ApiCallResult<ApiKeyRevealResponse>> RotateUserKeyAsync(int userId, CancellationToken ct = default);

    Task<ApiCallResult<ApiKeyRevealResponse>> ProvisionUserKeyAsync(int userId, CancellationToken ct = default);

    Task<ApiCallResult<bool>> RevokeUserKeyAsync(int userId, CancellationToken ct = default);

    // Admin / configuration — spec §4.6
    Task<ApiCallResult<IReadOnlyList<SystemConfigEntryResponse>>> GetConfigAsync(CancellationToken ct = default);

    Task<ApiCallResult<bool>> UpdateConfigAsync(string key, UpdateSystemConfigRequest request, CancellationToken ct = default);

    Task<ApiCallResult<PagedResult<AuditLogEntryResponse>>> GetAuditLogAsync(AuditLogQuery query, PagedRequest paging, CancellationToken ct = default);

    Task<ApiCallResult<DashboardSummaryResponse>> GetDashboardAsync(CancellationToken ct = default);
}
