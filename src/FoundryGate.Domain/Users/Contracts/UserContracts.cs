using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota.Contracts;

namespace FoundryGate.Domain.Users.Contracts;

/// <summary>A user as seen in admin lists/detail (spec &#167;3.1 <c>User</c>). GET /users (paged), GET /users/{id}.</summary>
/// <param name="UserId">Surrogate int PK.</param>
/// <param name="UserUnique">Externally-shared stable id.</param>
/// <param name="DisplayName">Name from Entra (spec &#167;7).</param>
/// <param name="Email">Email from Entra (spec &#167;7).</param>
/// <param name="EmployeeId">HR employee id from Entra, when available.</param>
/// <param name="IsActive">False when deactivated (spec &#167;5.3) or not found in the last Entra sync (spec &#167;7.2).</param>
/// <param name="IsUnlimited">When true, <paramref name="MonthlyTokenQuota"/> is ignored (spec &#167;3.2 step 1).</param>
/// <param name="MonthlyTokenQuota">Null means "use the system/group default" (spec &#167;3.2).</param>
/// <param name="IsApiKeyProvisioned">
/// Same derived fact as <see cref="ApiKeyResponse.IsProvisioned"/> — named to match it
/// exactly (rather than a "Has..." variant) so Web components can treat "is this user's
/// key provisioned" as one concept with one name everywhere it appears.
/// </param>
/// <param name="CreatedDate">When the user record was first created (spec &#167;7.1).</param>
/// <param name="LastSyncedDate">When this user was last touched by an Entra sync (spec &#167;7).</param>
public record UserResponse(
    int UserId,
    Guid UserUnique,
    string DisplayName,
    string Email,
    string? EmployeeId,
    bool IsActive,
    bool IsUnlimited,
    long? MonthlyTokenQuota,
    bool IsApiKeyProvisioned,
    DateTimeOffset CreatedDate,
    DateTimeOffset? LastSyncedDate);

/// <summary>
/// The caller's own profile: identity fields, current-period quota gauge, masked API
/// key, and the gateway connection info needed to configure a CLI. GET /users/me
/// (spec &#167;4.1: "get own profile, quota, key info").
/// </summary>
public record UserProfileResponse(
    int UserId,
    Guid UserUnique,
    string DisplayName,
    string Email,
    bool IsActive,
    bool IsUnlimited,
    QuotaAllocationResponse Quota,
    ApiKeyResponse ApiKey,
    GatewayConnectionInfo CliConfig);

/// <summary>
/// One group a user belongs to, as shown on <see cref="UserDetailResponse"/>. Deliberately a
/// user-side shape rather than <c>Groups.Contracts.GroupResponse</c>: the user detail view needs the
/// group's identity and when the person joined, not the group's quota policy or member count.
/// </summary>
/// <param name="GroupId">The group's int PK.</param>
/// <param name="GroupUnique">The group's externally-shared stable id.</param>
/// <param name="Name">The group's display name.</param>
/// <param name="AddedDate">When this membership was created.</param>
public record UserGroupMembershipResponse(
    int GroupId,
    Guid GroupUnique,
    string Name,
    DateTimeOffset AddedDate);

/// <summary>
/// Admin detail for one user — GET /users/{id}. The list row (<see cref="User"/>) plus the three
/// things an admin needs before changing anything: which groups feed this person's quota
/// resolution, what they actually resolved to this period, and whether they hold a gateway key.
/// </summary>
/// <param name="User">The same shape GET /users returns.</param>
/// <param name="CurrentAllocation">
/// The user's allocation for the current UTC calendar month, or <see langword="null"/> when none has
/// been resolved yet (nobody has called <c>GET /users/me</c> or <c>POST /quota/reset</c> for them this
/// month). Read-only: this endpoint never creates one.
/// </param>
/// <param name="ApiKey">The masked view of their APIM subscription key (<c>isProvisioned = false</c> when they have none).</param>
/// <param name="Groups">Every group the user belongs to, ordered by name.</param>
public record UserDetailResponse(
    UserResponse User,
    QuotaAllocationResponse? CurrentAllocation,
    ApiKeyResponse ApiKey,
    IReadOnlyList<UserGroupMembershipResponse> Groups);

/// <summary>
/// Filter parameters for GET /users. Bind alongside <see cref="Common.PagedRequest"/> via a separate
/// <c>[FromQuery]</c> parameter (the same shape <c>AuditLogQuery</c> uses).
/// </summary>
/// <param name="Search">
/// Case-insensitive substring match against display name or email; <see langword="null"/> or blank
/// matches everyone. Matching follows the database collation (SQL Server's default is
/// case-insensitive).
/// </param>
/// <param name="IsActive">When set, keeps only active (<c>true</c>) or only deactivated (<c>false</c>) users.</param>
public record UserListQuery(string? Search, bool? IsActive);

/// <summary>
/// PUT /users/{id}/quota body — admin sets a user's quota or unlimited flag.
/// Init-property record, not positional — see <see cref="Foundry.Contracts.CreateFoundryDeploymentRequest"/>'s remarks (#128).
/// </summary>
public record UpdateUserQuotaRequest
{
    /// <summary>When true, <see cref="MonthlyTokenQuota"/> is ignored (spec &#167;3.2 step 1).</summary>
    public bool IsUnlimited { get; init; }

    /// <summary>Null means "use the system/group default" (spec &#167;3.2 steps 2-5); ignored when <see cref="IsUnlimited"/> is true.</summary>
    [Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    public long? MonthlyTokenQuota { get; init; }
}

/// <summary>Result of POST /users/sync (spec &#167;7.2 bulk Entra sync).</summary>
/// <param name="AddedCount">Users assigned in Entra that had no row and were inserted (no API key).</param>
/// <param name="UpdatedCount">Users present in both whose directory fields and <c>LastSyncedDate</c> were refreshed (counted whether or not a field changed).</param>
/// <param name="DeactivatedCount">Previously active users no longer assigned in Entra that were set <c>IsActive = false</c>. Always <c>0</c> when <paramref name="SkippedGroupAssignmentCount"/> is non-zero.</param>
/// <param name="SkippedGroupAssignmentCount">
/// App-role assignments on the FoundryGate application whose principal is a <em>group</em>. Their members are
/// not expanded yet (#121), so the sync cannot tell a departed user from one covered by such a group — when this
/// is non-zero, departure detection is suspended for the run: nobody is deactivated, adds/updates still happen.
/// </param>
public record UserSyncResult(int AddedCount, int UpdatedCount, int DeactivatedCount, int SkippedGroupAssignmentCount);
