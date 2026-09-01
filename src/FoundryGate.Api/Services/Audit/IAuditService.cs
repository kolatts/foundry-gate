using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;

namespace FoundryGate.Api.Services.Audit;

/// <summary>
/// The Api-side face of the audit trail (spec &#167;11; issue #42): attributes a row to the
/// <em>current caller</em> and serves the admin <c>GET /audit</c> query. The actual row-building
/// lives in <see cref="IAuditWriter"/> (FoundryGate.Data) so Functions/Cli write identical rows;
/// this service is the thin wrapper that adds "who is calling" on top. Api code that already holds a
/// <c>UserId</c>, or is acting as the system (no human), uses <see cref="IAuditWriter"/> directly.
/// </summary>
/// <remarks>
/// <b>Nothing here saves.</b> <see cref="LogAsync"/> adds the row to the request's
/// <c>AppDbContext</c>; the calling service's own <c>SaveChangesAsync</c> persists the mutation and
/// its audit row atomically. Pattern at every call site: mutate → <c>await audit.LogAsync(...)</c> →
/// <c>await dbContext.SaveChangesAsync(ct)</c>. Details/constants guidance is on
/// <see cref="IAuditWriter"/>.
/// </remarks>
public interface IAuditService
{
    /// <summary>
    /// Adds an audit row attributed to the current caller (resolved through
    /// <c>ICurrentUserAccessor</c>, which also sees a <c>User</c> the caller has just <c>Add</c>ed but
    /// not yet saved — so first-login auto-provisioning gets user + <c>user.provisioned</c> row in one
    /// save).
    /// </summary>
    /// <param name="action">What happened — an <see cref="Domain.Constants.AuditActions"/> constant.</param>
    /// <param name="targetType">Kind of the affected record — an <see cref="Domain.Constants.AuditTargetTypes"/> constant; empty when there is no single target.</param>
    /// <param name="targetId">Identifier of the affected record as a string; empty when there is no single target.</param>
    /// <param name="details">Caller-defined detail object (before/after values), JSON-serialized; <see langword="null"/> stores an empty string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The added (not yet saved) <see cref="AuditLog"/> entity.</returns>
    /// <exception cref="UnauthorizedAccessException">
    /// The caller has no <c>User</c> row (→ 403, same as <c>ICurrentUserAccessor.GetRequiredUserAsync</c>):
    /// an authenticated principal without a row is an authorization-state problem, not a missing
    /// resource — they must call <c>GET /users/me</c> first.
    /// </exception>
    Task<AuditLog> LogAsync(string action, string targetType, string targetId, object? details, CancellationToken cancellationToken);

    /// <summary>
    /// Pages the audit trail newest-first (<c>OccurredDate</c> then <c>AuditLogId</c> descending),
    /// applying every non-null <paramref name="filter"/> member (exact match on actor/action/
    /// target; inclusive date range). Read-only projection — nothing is tracked.
    /// </summary>
    Task<PagedResult<AuditLogEntryResponse>> QueryAsync(AuditLogQuery filter, PagedRequest paging, CancellationToken cancellationToken);
}
