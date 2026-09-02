using FoundryGate.Domain.Common;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Web.Services;

/// <summary>
/// The filtered/detail reads and the Foundry surface the admin pages (#51, #52, #53, #62,
/// #63) need on top of the shell's original client (#48). Kept in its own partial file so
/// the two frontend waves can land next to each other without both rewriting one 100-line
/// interface; the routes and query names are `docs-site/src/content/docs/reference/api.md`.
/// </summary>
public partial interface IFoundryGateApiClient
{
    // Users — api.md §Users

    /// <summary>
    /// <c>GET /users?search=&amp;isActive=&amp;page=&amp;pageSize=</c> — the admin list behind
    /// <c>/users</c>'s grid. Blank <see cref="UserListQuery.Search"/> and null
    /// <see cref="UserListQuery.IsActive"/> mean "no filter" and are omitted from the query string.
    /// </summary>
    Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(UserListQuery query, PagedRequest paging, CancellationToken ct = default);

    /// <summary>
    /// <c>GET /users/{id}</c> — the list row plus group memberships, the current-period
    /// allocation (null when none has been resolved this month) and the masked key. This is
    /// what <c>/users/{id}</c> renders; the shell's <c>GetUserAsync</c> returns only the row.
    /// </summary>
    Task<ApiCallResult<UserDetailResponse>> GetUserDetailAsync(int userId, CancellationToken ct = default);

    // Groups — api.md §Groups

    /// <summary>
    /// <c>DELETE /groups/{id}?force=true</c> — deleting a group that still has members needs the
    /// force flag, so the UI can ask before sending it. <paramref name="force"/> false sends the
    /// plain delete and lets the API answer 409.
    /// </summary>
    Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, bool force, CancellationToken ct = default);

    // Quota — api.md §Quota

    /// <summary>
    /// <c>GET /quota/tiers</c> — the configured budget tiers. A quota is either unlimited or
    /// exactly one of these caps (fable-refactor-log D-013), so every quota editor in the admin
    /// UI is a pick from this list and every quota number is rendered as its
    /// <see cref="QuotaTierResponse.DisplayName"/>.
    /// </summary>
    Task<ApiCallResult<IReadOnlyList<QuotaTierResponse>>> GetQuotaTiersAsync(CancellationToken ct = default);

    // Quota increase requests — api.md §Quota Increase Requests

    /// <summary>
    /// <c>GET /requests?status=&amp;userId=&amp;page=&amp;pageSize=</c>. Any authenticated caller
    /// gets their own requests; an admin gets everyone's. <see cref="QuotaRequestQuery.UserId"/> is
    /// admin-only and omitted when null.
    /// </summary>
    Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(QuotaRequestQuery query, PagedRequest paging, CancellationToken ct = default);

    // Foundry — api.md §Foundry

    /// <summary><c>GET /foundry/deployments</c> — every deployment in every configured account (admin).</summary>
    Task<ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>> GetFoundryDeploymentsAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>POST /foundry/deployments</c> — create one OpenAI-format deployment. The API refuses
    /// Anthropic-format creates with a 400: Claude deployments are managed by infra end to end
    /// (plans/20-foundry-provisioning.md; lifting that is #126).
    /// </summary>
    Task<ApiCallResult<FoundryDeploymentResponse>> CreateFoundryDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken ct = default);

    /// <summary>
    /// <c>DELETE /foundry/deployments/{accountName}/{deploymentName}</c>. Anthropic-format
    /// deployments are refused with a 400 for the same reason creates are.
    /// </summary>
    Task<ApiCallResult<bool>> DeleteFoundryDeploymentAsync(string accountName, string deploymentName, CancellationToken ct = default);
}
