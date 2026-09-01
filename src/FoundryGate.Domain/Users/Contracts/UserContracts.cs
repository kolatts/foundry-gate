using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota.Contracts;

namespace FoundryGate.Domain.Users.Contracts;

/// <summary>A user as seen in admin lists/detail (spec &#167;3.1 <c>User</c>). GET /users (paged), GET /users/{id}.</summary>
public record UserResponse(
    int UserId,
    Guid UserUnique,
    string DisplayName,
    string Email,
    string? EmployeeId,
    bool IsActive,
    bool IsUnlimited,
    long? MonthlyTokenQuota,
    bool HasApiKey,
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

/// <summary>PUT /users/{id}/quota body — admin sets a user's quota or unlimited flag.</summary>
/// <param name="IsUnlimited">When true, <paramref name="MonthlyTokenQuota"/> is ignored (spec &#167;3.2 step 1).</param>
/// <param name="MonthlyTokenQuota">Null means "use the system/group default" (spec &#167;3.2 steps 2-5); ignored when <paramref name="IsUnlimited"/> is true.</param>
public record UpdateUserQuotaRequest(
    bool IsUnlimited,
    [property: Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    long? MonthlyTokenQuota);

/// <summary>Result of POST /users/sync (spec &#167;7.2 bulk Entra sync).</summary>
public record UserSyncResult(int AddedCount, int UpdatedCount, int DeactivatedCount);
