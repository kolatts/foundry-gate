using FoundryGate.Domain.Common;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The half of the fake that answers the admin management surface's own routes (#51, #52, #53,
/// #62, #63): the filtered user list, the forced group delete, and the three Foundry deployment
/// calls. A separate partial file for the same reason the client has one — the two frontend waves
/// add to this type independently.
/// </summary>
public sealed partial class FakeFoundryGateApiClient
{
    // -- Canned responses -----------------------------------------------------------------------

    public ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>> FoundryDeploymentsResult { get; set; } =
        ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>.Ok([]);

    /// <summary>
    /// What <c>GET /foundry/catalog</c> answers (#173). Defaults to empty, which is the "no
    /// suggestions, free text" path the create dialog has to keep working.
    /// </summary>
    public ApiCallResult<IReadOnlyList<FoundryCatalogEntryResponse>> FoundryCatalogResult { get; set; } =
        ApiCallResult<IReadOnlyList<FoundryCatalogEntryResponse>>.Ok([]);

    public ApiCallResult<FoundryDeploymentResponse> CreateFoundryDeploymentResult { get; set; } =
        ApiCallResult<FoundryDeploymentResponse>.Fail(ApiCallStatus.Error, "No create result arranged for this test.");

    /// <summary>
    /// What <c>POST /groups/sync-entra</c> answers. A summary, not a status: the run keeps going
    /// past a failure, so failed groups arrive as rows with <c>Succeeded = false</c> inside a 200.
    /// </summary>
    public ApiCallResult<IReadOnlyList<GroupSyncResult>> GroupsSyncResult { get; set; } =
        ApiCallResult<IReadOnlyList<GroupSyncResult>>.Ok([]);

    // -- Captured arguments ---------------------------------------------------------------------

    /// <summary>
    /// What <c>GET /users/sync/last</c> answers (#171). Defaults to "this fork has never run one",
    /// which is what a freshly deployed fork sees.
    /// </summary>
    public ApiCallResult<UserSyncStatusResponse> LastUserSyncResult { get; set; } =
        ApiCallResult<UserSyncStatusResponse>.Ok(new UserSyncStatusResponse(null, null));

    /// <summary>Every filtered <c>GET /users</c> the pages made, in order — search, status and paging.</summary>
    public List<(UserListQuery Query, PagedRequest Paging)> UserListCalls { get; } = [];

    /// <summary>
    /// Group deletes with the flag they carried. <c>DeletedGroupIds</c> records the ids alone; this
    /// records whether the caller sent <c>?force=true</c>, which is the part that decides whether a
    /// group with members is deleted or 409s.
    /// </summary>
    public List<(int GroupId, bool Force)> DeletedGroups { get; } = [];

    public List<CreateFoundryDeploymentRequest> CreatedDeployments { get; } = [];

    public List<(string AccountName, string DeploymentName)> DeletedDeployments { get; } = [];

    // -- Fluent arrange helpers -----------------------------------------------------------------

    /// <summary>What <c>GET /foundry/deployments</c> returns.</summary>
    public FakeFoundryGateApiClient ArrangeDeployments(params FoundryDeploymentResponse[] deployments)
    {
        FoundryDeploymentsResult = ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>.Ok(deployments);
        return this;
    }

    /// <summary>What <c>GET /foundry/catalog</c> returns.</summary>
    public FakeFoundryGateApiClient ArrangeCatalog(params FoundryCatalogEntryResponse[] entries)
    {
        FoundryCatalogResult = ApiCallResult<IReadOnlyList<FoundryCatalogEntryResponse>>.Ok(entries);
        return this;
    }

    // -- IFoundryGateApiClient ------------------------------------------------------------------

    public Task<ApiCallResult<PagedResult<UserResponse>>> GetUsersAsync(UserListQuery query, PagedRequest paging, CancellationToken ct = default)
    {
        UserListCalls.Add((query, paging));
        return RespondAsync(nameof(GetUsersAsync), UsersResult);
    }

    public Task<ApiCallResult<UserSyncStatusResponse>> GetLastUserSyncAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetLastUserSyncAsync), LastUserSyncResult);

    public Task<ApiCallResult<bool>> DeleteGroupAsync(int groupId, bool force, CancellationToken ct = default)
    {
        DeletedGroups.Add((groupId, force));
        DeletedGroupIds.Add(groupId);
        return RespondAsync(nameof(DeleteGroupAsync), MutationResult);
    }

    public Task<ApiCallResult<IReadOnlyList<FoundryDeploymentResponse>>> GetFoundryDeploymentsAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetFoundryDeploymentsAsync), FoundryDeploymentsResult);

    public Task<ApiCallResult<IReadOnlyList<FoundryCatalogEntryResponse>>> GetFoundryCatalogAsync(CancellationToken ct = default) =>
        RespondAsync(nameof(GetFoundryCatalogAsync), FoundryCatalogResult);

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
}
