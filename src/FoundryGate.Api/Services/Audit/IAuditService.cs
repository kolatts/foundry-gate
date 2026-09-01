using FoundryGate.Data.Entities;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;

namespace FoundryGate.Api.Services.Audit;

/// <summary>
/// Writes and reads the <see cref="AuditLog"/> trail (spec &#167;11: "all admin actions, key rotations,
/// approvals written to <c>AuditLog</c>"; issue #42).
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes are not saved here.</b> <c>LogAsync</c> only <em>adds</em> the row to the request's
/// <c>AppDbContext</c>; the calling service's own <c>SaveChangesAsync</c> persists the mutation and
/// its audit row in one transaction. That is deliberate: a fire-and-forget/"fire-and-log" audit
/// (separate save, swallowed failure) can leave a mutation with no audit row or an audit row for a
/// mutation that rolled back — either is worse for an audit trail than failing the request. The
/// pattern at every call site is therefore: mutate → <c>await audit.LogAsync(...)</c> →
/// <c>await dbContext.SaveChangesAsync(ct)</c>.
/// </para>
/// <para>
/// <c>action</c> and <c>targetType</c> should be constants from
/// <see cref="Domain.Constants.AuditActions"/> / <see cref="Domain.Constants.AuditTargetTypes"/>.
/// <c>details</c> is any serializable object (an anonymous <c>new { before, after }</c> is the
/// expected shape); it is JSON-serialized with web (camelCase) defaults into
/// <see cref="AuditLog.Details"/>. Never put a secret (an APIM key, a token) in it.
/// </para>
/// </remarks>
public interface IAuditService
{
    /// <summary>
    /// Adds an audit row attributed to the current caller (via <c>ICurrentUserAccessor</c>).
    /// </summary>
    /// <param name="action">What happened — an <see cref="Domain.Constants.AuditActions"/> constant.</param>
    /// <param name="targetType">Kind of the affected record — an <see cref="Domain.Constants.AuditTargetTypes"/> constant; empty when there is no single target.</param>
    /// <param name="targetId">Identifier of the affected record as a string; empty when there is no single target.</param>
    /// <param name="details">Caller-defined detail object (before/after values), JSON-serialized; <see langword="null"/> stores an empty string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The added (not yet saved) <see cref="AuditLog"/> entity.</returns>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row yet (→ 403): every human-attributed audit row needs a resolvable actor, and callers are provisioned by <c>GET /users/me</c> before they can act.</exception>
    Task<AuditLog> LogAsync(string action, string targetType, string targetId, object? details, CancellationToken cancellationToken);

    /// <summary>
    /// Adds an audit row with an explicit actor — <see langword="null"/> for system-initiated actions
    /// with no human behind them (the monthly reset, usage sync, Entra sync jobs) or a known
    /// <c>UserId</c> when the caller has already resolved it.
    /// </summary>
    /// <param name="actorUserId">The acting user's <c>UserId</c>, or <see langword="null"/> for a system actor.</param>
    /// <param name="action">What happened — an <see cref="Domain.Constants.AuditActions"/> constant.</param>
    /// <param name="targetType">Kind of the affected record — an <see cref="Domain.Constants.AuditTargetTypes"/> constant; empty when there is no single target.</param>
    /// <param name="targetId">Identifier of the affected record as a string; empty when there is no single target.</param>
    /// <param name="details">Caller-defined detail object, JSON-serialized; <see langword="null"/> stores an empty string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The added (not yet saved) <see cref="AuditLog"/> entity.</returns>
    Task<AuditLog> LogAsync(int? actorUserId, string action, string targetType, string targetId, object? details, CancellationToken cancellationToken);

    /// <summary>
    /// Pages the audit trail newest-first (<c>OccurredDate</c> then <c>AuditLogId</c> descending),
    /// applying every non-null <paramref name="filter"/> member (exact match on actor/action/
    /// target; inclusive date range). Read-only projection — nothing is tracked.
    /// </summary>
    Task<PagedResult<AuditLogEntryResponse>> QueryAsync(AuditLogQuery filter, PagedRequest paging, CancellationToken cancellationToken);
}
