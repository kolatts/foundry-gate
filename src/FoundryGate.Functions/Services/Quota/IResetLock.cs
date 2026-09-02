namespace FoundryGate.Functions.Services.Quota;

/// <summary>
/// A short-lived mutual exclusion for a scheduled job, so two Function App replicas that wake at the
/// same instant do not both run it (#38).
/// </summary>
/// <remarks>
/// <para>
/// <b>Belt, not correctness.</b> The reset is idempotent by construction — a second run re-resolves
/// the same allocations onto the same rows — and a Timer trigger is already leased singleton across
/// instances by the Functions host. This exists because "already correct twice over" is what you want
/// from the job that touches every developer's budget, and because it also keeps a scheduled run from
/// interleaving with an admin's <c>POST /quota/reset</c> if the fork ever points that at the same lock.
/// </para>
/// <para>
/// <b>Never fail the job.</b> An implementation that cannot reach its store returns "not acquired"
/// and logs; it does not throw. A monthly reset skipped because storage was down would be a far worse
/// outcome than one that ran twice, and the next daily tick retries anyway (#165).
/// </para>
/// </remarks>
public interface IResetLock
{
    /// <summary>
    /// Tries to take the lock named <paramref name="lockName"/>. Dispose the returned handle to
    /// release it; a handle whose <see cref="IResetLockHandle.IsAcquired"/> is <see langword="false"/>
    /// means someone else holds it and the caller should do nothing.
    /// </summary>
    Task<IResetLockHandle> TryAcquireAsync(string lockName, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="IResetLock.TryAcquireAsync"/>; disposing releases an acquired lock.</summary>
public interface IResetLockHandle : IAsyncDisposable
{
    /// <summary><see langword="true"/> when this caller holds the lock and may run the job.</summary>
    bool IsAcquired { get; }
}
