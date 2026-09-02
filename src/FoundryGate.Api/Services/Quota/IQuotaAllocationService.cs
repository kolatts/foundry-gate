using FoundryGate.Domain.Common;
using FoundryGate.Domain.Quota.Contracts;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// The <c>/api/v1/quota</c> surface (issue #33): current-period allocation reads and the idempotent
/// manual reset. "Current period" is always the UTC calendar month per the injected
/// <see cref="TimeProvider"/> (<see cref="Domain.Quota.BillingPeriod.Current"/>).
/// </summary>
public interface IQuotaAllocationService
{
    /// <summary>
    /// The configured budget tiers (finite tiers by ascending cap, then unlimited) — the only values a
    /// monthly token quota may take (D-013). Any authenticated user; the UI offers these as the choices.
    /// </summary>
    IReadOnlyList<QuotaTierResponse> ListTiers();

    /// <summary>
    /// Admin: every allocation for the current period, ordered by user display name then <c>UserId</c>,
    /// with the owning user's name/email projected in. Rows exist only for users who have been
    /// resolved this period (first <c>/me</c> of the month or a reset) — this lists allocations, not users.
    /// </summary>
    Task<PagedResult<QuotaAllocationResponse>> ListCurrentPeriodAsync(PagedRequest paging, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's own current-period allocation. If none exists yet it is resolved, created (with
    /// <c>TokensUsed = 0</c>, no <c>ResetDate</c>) and saved before returning, so a developer's first
    /// visit of the month always has a gauge to show. Not audited: the row is derived state, not an
    /// admin action.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row yet (→ 403; call <c>GET /users/me</c> first), or their account is deactivated (→ 403; no allocation is issued to an inactive user).</exception>
    Task<QuotaAllocationResponse> GetMyAllocationAsync(CancellationToken cancellationToken);

    /// <summary>Admin: one user's current-period allocation. Read-only — does not create a missing row.</summary>
    /// <exception cref="KeyNotFoundException">No such user, or the user has no allocation for the current period yet (→ 404).</exception>
    Task<QuotaAllocationResponse> GetUserAllocationAsync(int userId, CancellationToken cancellationToken);

    /// <summary>
    /// One user's current-period allocation, or <see langword="null"/> when they have none yet — the
    /// same read as <see cref="GetUserAllocationAsync"/> without the 404, for composite views
    /// (<c>GET /users/{id}</c>) where "no allocation this month" is a field, not a failed request.
    /// Read-only: never creates a row, never checks that the user exists.
    /// </summary>
    Task<QuotaAllocationResponse?> FindUserAllocationAsync(int userId, CancellationToken cancellationToken);

    /// <summary>
    /// Admin, idempotent: re-resolves every active user's allocation for the current UTC calendar
    /// month in one unit of work. New rows start at <c>TokensUsed = 0</c>; existing rows are re-resolved
    /// but keep their reconciled <c>TokensUsed</c> (the gateway's monthly window resets itself — #10
    /// direction update — so zeroing the mirror mid-month would only make the dashboard lie). Every
    /// touched row gets <c>IsHardStopped = false</c> and <c>ResetDate = now</c>. Exactly one audit row
    /// (<c>AuditActions.QuotaAllocationReset</c>, attributed to the calling admin) per run, committed
    /// atomically with the rows. Running it twice in a month yields the same row count.
    /// </summary>
    Task<QuotaResetResult> ResetAsync(CancellationToken cancellationToken);
}
