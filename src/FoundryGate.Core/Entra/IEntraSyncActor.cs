using FoundryGate.Data.Entities;

namespace FoundryGate.Core.Entra;

/// <summary>
/// Who a directory-sync run's audit row is attributed to. A seam rather than a direct dependency
/// because the answer is a <em>host</em> question and Core must not know about either answer: in the
/// Api it is the admin who called <c>POST /users/sync</c> or <c>POST /groups/sync-entra</c>, in the
/// Functions host there is no human at all — the nightly <c>EntraSyncFunction</c> acts as the system
/// (#151).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="Quota.IGatewayTierSyncActor"/>, and for the same reason
/// (CONVENTIONS.md §Solution structure: "prefer a narrow seam for the difference over a second
/// implementation of the whole thing"). <see langword="null"/> means "the system" and produces an
/// <c>ActorUserId IS NULL</c> row via <see cref="Data.Audit.IAuditWriter.AddSystem"/>; a non-null
/// <see cref="User"/> produces an attributed row via the navigation overload
/// <see cref="Data.Audit.IAuditWriter.Add(User, string, string, string, object?)"/>.
/// </para>
/// <para>
/// <b>When it is called differs between the two syncs, on purpose.</b> Group sync resolves the actor
/// <em>first</em>, so an unprovisioned admin's 403 lands before Graph is read and long before quota
/// resolution can move anybody's APIM product. User sync resolves it at the end, where
/// <c>IAuditService.LogAsync</c> used to: the Api's accessor sees a <c>User</c> the run has just
/// <c>Add</c>ed but not yet saved, which is what lets an admin whose own row is being imported by
/// their first sync attribute that run to themselves instead of being refused by it.
/// </para>
/// </remarks>
public interface IEntraSyncActor
{
    /// <summary>The acting user, or <see langword="null"/> when the run is the system's own.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="UnauthorizedAccessException">The host requires an actor and this caller has none (the Api's 403, "call GET /users/me first").</exception>
    Task<User?> ResolveActorAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The <see cref="IEntraSyncActor"/> for a host whose sync runs are never anybody's request: the
/// nightly <c>EntraSyncFunction</c> (#151). Always <see langword="null"/>, so every row a scheduled
/// run causes is system-attributed — the same shape the monthly reset's <c>quota.monthly-reset</c>
/// row has.
/// </summary>
public sealed class SystemEntraSyncActor : IEntraSyncActor
{
    /// <inheritdoc />
    public Task<User?> ResolveActorAsync(CancellationToken cancellationToken) => Task.FromResult<User?>(null);
}
