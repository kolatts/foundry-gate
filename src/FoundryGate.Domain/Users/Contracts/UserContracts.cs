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
/// <param name="LastLoginDate">
/// When this person last loaded their own profile; <see langword="null"/> means they have never
/// signed in — the signal an offboarding or licence review wants (#167). Accurate to within
/// <c>UserService.LastLoginGranularity</c>, not to the second: a profile load is only a write when
/// the stored value is that stale, so <c>GET /users/me</c> stays a read in the common case.
/// </param>
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
    DateTimeOffset? LastSyncedDate,
    DateTimeOffset? LastLoginDate);

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
/// Group-principal app-role assignments whose member expansion <em>failed</em> this run (Graph refused, or the
/// group no longer exists). Groups are expanded to their transitive user members normally (#121), so this is
/// <c>0</c> on a healthy run even when every developer is assigned through a group. Non-zero means the run saw
/// only part of the population, so departure detection is suspended for it: nobody is deactivated, adds/updates
/// still happen, and the offending groups are named in the log and the <c>users.synced</c> audit row.
/// </param>
/// <param name="FailedCount">
/// Departed users whose deprovision could not be completed because the gateway refused the deletion
/// (a <c>502</c>-class failure). Each runs in its own unit of work, so a failure here costs only that
/// user: everyone else is still processed, and the next run retries them (revocation is idempotent on
/// a subscription that is already gone). Non-zero means someone still holds a working key.
/// </param>
public record UserSyncResult(int AddedCount, int UpdatedCount, int DeactivatedCount, int SkippedGroupAssignmentCount, int FailedCount);

/// <summary>
/// What the last <c>POST /users/sync</c> did and when — <c>GET /users/sync/last</c>, admin-only
/// (#171). Read from the <c>LastUserSyncDate</c> / <c>LastUserSyncResult</c> configuration rows the
/// sync writes in its own unit of work, so it survives the browser session, covers runs triggered
/// outside the UI, and is there on a cold page load.
/// </summary>
/// <param name="LastSyncDate">
/// When the last run finished, or <see langword="null"/> if this fork has never run one (or ran one
/// before #171 shipped — nothing backfills history that was never recorded).
/// </param>
/// <param name="LastResult">
/// That run's counts, or <see langword="null"/> when there is no recorded run — and also when the
/// stored JSON cannot be read, which is reported as "no result" rather than as an error: a broken
/// souvenir of a past run must not fail the page that shows it.
/// </param>
public record UserSyncStatusResponse(DateTimeOffset? LastSyncDate, UserSyncResult? LastResult);
