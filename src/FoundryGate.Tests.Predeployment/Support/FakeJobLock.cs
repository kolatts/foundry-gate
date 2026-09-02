using FoundryGate.Functions.Services.Jobs;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// Hand-rolled <see cref="IJobLock"/> (CONVENTIONS.md: no mocking library) that grants or refuses on
/// demand and records what was asked for and whether the handle was released — the two things the
/// monthly reset's use of a lock has to get right.
/// </summary>
public sealed class FakeJobLock(bool acquire = true) : IJobLock
{
    /// <summary>Lock names this fake was asked for, in order.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>How many acquired handles were disposed — a lease the job forgot to release would show up as a gap here.</summary>
    public int Released { get; private set; }

    /// <inheritdoc />
    public Task<IJobLockHandle> TryAcquireAsync(string lockName, CancellationToken cancellationToken)
    {
        Requested.Add(lockName);

        return Task.FromResult<IJobLockHandle>(acquire ? new Handle(this) : new Handle(null));
    }

    private sealed class Handle(FakeJobLock? owner) : IJobLockHandle
    {
        public bool IsAcquired => owner is not null;

        public ValueTask DisposeAsync()
        {
            if (owner is not null)
            {
                owner.Released++;
            }

            return ValueTask.CompletedTask;
        }
    }
}
