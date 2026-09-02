using FoundryGate.Domain.Common;
using FoundryGate.Domain.Foundry.Contracts;
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
    /// <c>GET /users/sync/last</c> — when <c>POST /users/sync</c> last ran and what it did (#171).
    /// Read from configuration rows the sync writes itself, so <c>/users/sync</c> can show the previous
    /// run on a cold load, including one triggered outside this browser. Both fields are null on a fork
    /// that has never run one.
    /// </summary>
    Task<ApiCallResult<UserSyncStatusResponse>> GetLastUserSyncAsync(CancellationToken ct = default);

    // Groups — api.md §Groups

    /// <summary>
    /// <c>DELETE /groups/{id}?force=true</c> — deleting a group that still has members needs the
    /// force flag, so the UI can ask before sending it. <paramref name="force"/> false sends the
    /// plain delete and lets the API answer 409.
    /// </summary>
    Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, bool force, CancellationToken ct = default);

    // Foundry — api.md §Foundry

    /// <summary><c>GET /foundry/deployments</c> — every deployment in every configured account (admin).</summary>
    Task<ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>> GetFoundryDeploymentsAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>GET /foundry/catalog</c> — the models the configured accounts can serve, with their SKUs
    /// and a suggested capacity (#173). What the create dialog's pickers offer instead of a hardcoded
    /// array. A failure here is not fatal to the dialog: both fields still accept anything typed.
    /// </summary>
    Task<ApiCallResult<IReadOnlyList<FoundryCatalogEntryResponse>>> GetFoundryCatalogAsync(CancellationToken ct = default);

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
