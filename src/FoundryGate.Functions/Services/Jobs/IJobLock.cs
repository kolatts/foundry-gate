namespace FoundryGate.Functions.Services.Jobs;

/// <summary>
/// A short-lived mutual exclusion for a scheduled job, so two Function App replicas that wake at the
/// same instant do not both run it (#38).
/// </summary>
/// <remarks>
/// <para>
/// <b>Belt, not correctness.</b> Every job that takes one of these is idempotent by construction — the
/// monthly reset re-resolves the same allocations onto the same rows (#38), the nightly Entra sync is
/// a pull-only reconciliation (#151) — and a Timer trigger is already leased singleton across
/// instances by the Functions host. This exists because "already correct twice over" is what you want
/// from the jobs that touch every developer's budget and every developer's account, and because it
/// also keeps a scheduled run from interleaving with the admin endpoint that does the same thing if a
/// fork ever points that at the same lock.
/// </para>
/// <para>
/// <b>One lock per job, by name.</b> Callers pass their own <c>LockName</c> constant, so the reset and
/// the directory sync never block each other; the blob implementation leases a blob of that name.
/// </para>
/// <para>
/// <b>Never fail the job.</b> An implementation that cannot reach its store returns "not acquired"
/// and logs; it does not throw. A monthly reset skipped because storage was down would be a far worse
/// outcome than one that ran twice, and the next tick retries anyway (#165).
/// </para>
/// <para>
/// Named <c>IResetLock</c> until #151, when a second scheduled job needed exactly the same primitive.
/// </para>
/// </remarks>
public interface IJobLock
{
    /// <summary>
    /// Tries to take the lock named <paramref name="lockName"/>. Dispose the returned handle to
    /// release it; a handle whose <see cref="IJobLockHandle.IsAcquired"/> is <see langword="false"/>
    /// means someone else holds it and the caller should do nothing.
    /// </summary>
    Task<IJobLockHandle> TryAcquireAsync(string lockName, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="IJobLock.TryAcquireAsync"/>; disposing releases an acquired lock.</summary>
public interface IJobLockHandle : IAsyncDisposable
{
    /// <summary><see langword="true"/> when this caller holds the lock and may run the job.</summary>
    bool IsAcquired { get; }
}
