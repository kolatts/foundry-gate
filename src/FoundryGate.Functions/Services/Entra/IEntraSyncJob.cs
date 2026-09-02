using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Functions.Services.Entra;

/// <summary>
/// Everything <c>EntraSyncFunction</c> does, minus the trigger attribute (#151): check the off switch,
/// take the cross-replica lock, run the users sync and then the group sync, and write one audit row
/// describing both. Split out so the behaviour is unit-testable without a Functions host — the same
/// shape as <c>IMonthlyResetJob</c>.
/// </summary>
public interface IEntraSyncJob
{
    /// <summary>
    /// Runs one nightly pass. Never throws for "the directory is off on this host" or "another replica
    /// has it" — those are outcomes, not failures. A Graph or database fault <em>does</em> propagate,
    /// so the Functions host records a failed invocation and the next night retries (both syncs are
    /// pull-only and idempotent).
    /// </summary>
    Task<EntraSyncOutcome> RunAsync(CancellationToken cancellationToken);
}

/// <summary>What one nightly pass did.</summary>
/// <param name="SkipReasonType">Why nothing ran; <see cref="EntraSyncSkipReasonType.None"/> when it did.</param>
/// <param name="Users">The users sync's own result, or <see langword="null"/> when the pass was skipped.</param>
/// <param name="Groups">
/// One result per linked group, or <see langword="null"/> when the pass was skipped. Empty on a fork
/// with no Entra-linked groups, which is the normal shape until an admin links one.
/// </param>
public readonly record struct EntraSyncOutcome(
    EntraSyncSkipReasonType SkipReasonType,
    UserSyncResult? Users,
    IReadOnlyList<GroupSyncResult>? Groups)
{
    /// <summary><see langword="true"/> when both syncs ran.</summary>
    public bool Ran => SkipReasonType == EntraSyncSkipReasonType.None;
}

/// <summary>Why a nightly pass did nothing.</summary>
public enum EntraSyncSkipReasonType
{
    /// <summary>It did not skip — both syncs ran.</summary>
    None = 0,

    /// <summary>
    /// <c>Entra:Enabled</c> is false on this host. Logged once at Information, not Warning or Error:
    /// a fork that has not granted the Graph application roles has deliberately left the feature off,
    /// and a nightly error would train everyone to ignore this job's alerts (#151).
    /// </summary>
    DirectoryDisabled = 1,

    /// <summary>Another replica holds the lock. It is doing the work; this one does nothing.</summary>
    LockNotAcquired = 2,
}
