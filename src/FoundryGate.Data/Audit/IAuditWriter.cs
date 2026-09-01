using FoundryGate.Data.Entities;

namespace FoundryGate.Data.Audit;

/// <summary>
/// The one audit-row writer for every host (Api, Functions, Cli). Lives in Data — not Api — because
/// system jobs in <c>FoundryGate.Functions</c> (monthly reset, usage sync, Entra sync) reference Data
/// and Domain only, yet must write the same rows the Api does; two hosts must never grow two
/// writers. Api's <c>IAuditService</c> is a thin wrapper that resolves the current caller and
/// delegates here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here saves.</b> Each method only <em>adds</em> the row to the shared
/// <see cref="AppDbContext"/>; the caller's own <c>SaveChangesAsync</c> persists the mutation and its
/// audit row in one transaction. A fire-and-forget audit (separate save, swallowed failure) can leave
/// a mutation with no audit row or an audit row for a rolled-back mutation — for an audit trail,
/// failing the request is the correct outcome. Pattern at every call site: mutate →
/// <c>audit.Add(...)</c> → <c>await dbContext.SaveChangesAsync(ct)</c>.
/// </para>
/// <para>
/// The methods are synchronous because they touch only the change tracker — there is no I/O to await.
/// <c>action</c>/<c>targetType</c> should be constants from Domain's <c>AuditActions</c> /
/// <c>AuditTargetTypes</c>. <c>details</c> is any serializable object (an anonymous
/// <c>new { before, after }</c> is the expected shape), JSON-serialized with web (camelCase) defaults
/// and cycle-tolerant reference handling into <see cref="AuditLog.Details"/>. Prefer projecting the
/// fields you mean over passing a tracked entity — an entity graph serializes its navigations too.
/// Never put a secret (an APIM key, a token) in it.
/// </para>
/// </remarks>
public interface IAuditWriter
{
    /// <summary>
    /// Adds a row attributed to <paramref name="actor"/> via the <see cref="AuditLog.ActorUser"/>
    /// navigation — so an actor that has itself just been <c>Add</c>ed and not yet saved (first-login
    /// auto-provisioning) is attributed correctly once the caller saves; EF fixes up the FK then.
    /// </summary>
    /// <param name="actor">The acting user (tracked, saved or not).</param>
    /// <param name="action">What happened — an <c>AuditActions</c> constant.</param>
    /// <param name="targetType">Kind of the affected record — an <c>AuditTargetTypes</c> constant; empty when there is no single target.</param>
    /// <param name="targetId">Identifier of the affected record as a string; empty when there is no single target.</param>
    /// <param name="details">Caller-defined detail object (before/after values), JSON-serialized; <see langword="null"/> stores an empty string.</param>
    /// <returns>The added (not yet saved) <see cref="AuditLog"/> entity.</returns>
    AuditLog Add(User actor, string action, string targetType, string targetId, object? details);

    /// <summary>Adds a row attributed to an already-persisted user by <paramref name="actorUserId"/>.</summary>
    /// <param name="actorUserId">The acting user's <c>UserId</c>.</param>
    /// <param name="action">What happened — an <c>AuditActions</c> constant.</param>
    /// <param name="targetType">Kind of the affected record — an <c>AuditTargetTypes</c> constant; empty when there is no single target.</param>
    /// <param name="targetId">Identifier of the affected record as a string; empty when there is no single target.</param>
    /// <param name="details">Caller-defined detail object, JSON-serialized; <see langword="null"/> stores an empty string.</param>
    /// <returns>The added (not yet saved) <see cref="AuditLog"/> entity.</returns>
    AuditLog Add(int actorUserId, string action, string targetType, string targetId, object? details);

    /// <summary>
    /// Adds a row with no human actor (<see cref="AuditLog.ActorUserId"/> = <see langword="null"/>) —
    /// the monthly reset, usage sync, and Entra sync jobs.
    /// </summary>
    /// <param name="action">What happened — an <c>AuditActions</c> constant.</param>
    /// <param name="targetType">Kind of the affected record — an <c>AuditTargetTypes</c> constant; empty when there is no single target.</param>
    /// <param name="targetId">Identifier of the affected record as a string; empty when there is no single target.</param>
    /// <param name="details">Caller-defined detail object, JSON-serialized; <see langword="null"/> stores an empty string.</param>
    /// <returns>The added (not yet saved) <see cref="AuditLog"/> entity.</returns>
    AuditLog AddSystem(string action, string targetType, string targetId, object? details);
}
