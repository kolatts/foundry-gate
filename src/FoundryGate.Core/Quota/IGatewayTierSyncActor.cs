using FoundryGate.Data.Entities;

namespace FoundryGate.Core.Quota;

/// <summary>
/// Who the <c>key.tier-changed</c> row a tier move writes is attributed to. A seam rather than a
/// direct dependency because the answer is a <em>host</em> question and Core must not know about
/// either answer: in the Api the actor is the admin whose request moved the quota, in the Functions
/// host there is no human at all — the monthly reset acts as the system.
/// </summary>
/// <remarks>
/// <para>
/// <see langword="null"/> means "the system" and produces an <c>ActorUserId IS NULL</c> row via
/// <see cref="Data.Audit.IAuditWriter.AddSystem"/>; a non-null <see cref="User"/> produces an
/// attributed row via <see cref="Data.Audit.IAuditWriter.Add(User, string, string, string, object?)"/>
/// (the navigation overload, so an actor that is itself unsaved still attributes correctly).
/// </para>
/// <para>
/// <b>Resolved before the gateway is touched.</b> <see cref="ApimGatewayTierSync"/> calls this ahead
/// of any ARM call, so an implementation that refuses — the Api's, when the caller has no
/// <c>User</c> row yet — refuses <em>before</em> the subscription has moved, rather than leaving a
/// re-scoped subscription with no audit row (CONVENTIONS.md §External side effects have a commit
/// point).
/// </para>
/// </remarks>
public interface IGatewayTierSyncActor
{
    /// <summary>The acting user, or <see langword="null"/> when the change is the system's own.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="UnauthorizedAccessException">The host requires an actor and this caller has none (the Api's 403 "call GET /users/me first").</exception>
    Task<User?> ResolveActorAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The <see cref="IGatewayTierSyncActor"/> for a host whose tier moves are never anybody's request:
/// the Functions jobs (the monthly reset, #38). Always <see langword="null"/>, so every row it causes
/// is system-attributed — the same shape the reset's own <c>quota.monthly-reset</c> row has.
/// </summary>
public sealed class SystemGatewayTierSyncActor : IGatewayTierSyncActor
{
    /// <inheritdoc />
    public Task<User?> ResolveActorAsync(CancellationToken cancellationToken) => Task.FromResult<User?>(null);
}
