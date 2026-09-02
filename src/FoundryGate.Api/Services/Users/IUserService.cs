using FoundryGate.Domain.Common;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Api.Services.Users;

/// <summary>
/// The <c>/api/v1/users</c> surface other than the Entra bulk sync (issues #28 and #29): the
/// developer-facing <c>GET /users/me</c> and the admin list / detail / quota / activate / deactivate
/// actions. Every lifecycle-changing action here is a thin call into
/// <see cref="Lifecycle.IUserLifecycleService"/> — this service owns reads, projections and the quota
/// write; it never re-implements a provision or deprovision sequence.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// The caller's own profile, <b>auto-provisioning them on first call</b> (spec &#167;7.1): with no
    /// <c>User</c> row this runs the full provision pipeline — create the row from token claims
    /// (enriched from the directory when <c>Entra:Enabled</c>), resolve this month's quota, mint the
    /// APIM subscription, audit <c>user.provisioned</c> — and with one it refreshes the display fields
    /// from the token, stamps <c>LastSyncedDate</c>, and resolves this month's allocation if the month
    /// just turned over. Idempotent from the second call on.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller's account is deactivated (→ 403): an admin must re-activate it before the profile is available again.</exception>
    /// <exception cref="ConflictException">Two first logins for the same oid raced (→ 409, retry).</exception>
    /// <exception cref="FeatureNotConfiguredException">First login on a host where APIM is not configured (→ 503): no user is created, because a user with no key cannot call the gateway.</exception>
    /// <exception cref="UpstreamDependencyException">First login where APIM or Microsoft Graph failed (→ 502); no user is created.</exception>
    Task<UserProfileResponse> GetMyProfileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Admin: users ordered by display name then id, paged, filtered by
    /// <see cref="UserListQuery.Search"/> (substring of display name or email) and
    /// <see cref="UserListQuery.IsActive"/>. Read-only projection.
    /// </summary>
    Task<PagedResult<UserResponse>> ListAsync(UserListQuery filter, PagedRequest paging, CancellationToken cancellationToken);

    /// <summary>Admin: one user with their group memberships, current-period allocation (null when none exists yet) and masked key.</summary>
    /// <exception cref="KeyNotFoundException">No such user (→ 404).</exception>
    Task<UserDetailResponse> GetAsync(int userId, CancellationToken cancellationToken);

    /// <summary>
    /// Admin: sets the user-level quota override and re-resolves the current period, which moves their
    /// APIM subscription to the new tier product if it changed (<c>IGatewayTierSync</c>). Audits
    /// <c>user.quota-changed</c> with before/after.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such user (→ 404).</exception>
    /// <exception cref="ArgumentException">The quota is neither unlimited nor exactly one configured tier cap (→ 400; the message lists the allowed values).</exception>
    Task<UserResponse> UpdateQuotaAsync(int userId, UpdateUserQuotaRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Admin: re-activates a deactivated user through the full provision pipeline (plan 21 Trigger C) —
    /// <c>IsActive = true</c>, quota re-resolved, a new APIM subscription minted (adopting an orphan of
    /// the same name if one survived a previous deprovision). The minted key is deliberately
    /// <em>not</em> returned: the developer reads it themselves with <c>POST /keys/me/reveal</c>, so no
    /// admin response ever carries someone else's key material.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such user (→ 404).</exception>
    /// <exception cref="ConflictException">The user is already active (→ 409).</exception>
    Task<UserResponse> ActivateAsync(int userId, CancellationToken cancellationToken);

    /// <summary>
    /// Admin: deactivates a user through the full deprovision pipeline (plan 21 Trigger A) — APIM
    /// subscription deleted, key fields cleared, <c>IsActive = false</c>, current allocation
    /// hard-stopped, pending increase requests rejected, <c>user.deactivated</c> audited.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such user (→ 404).</exception>
    /// <exception cref="ConflictException">The user is already deactivated (→ 409).</exception>
    Task<UserResponse> DeactivateAsync(int userId, CancellationToken cancellationToken);
}
