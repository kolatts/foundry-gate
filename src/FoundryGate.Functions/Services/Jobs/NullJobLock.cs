using Microsoft.Extensions.Logging;

namespace FoundryGate.Functions.Services.Jobs;

/// <summary>
/// The <see cref="IJobLock"/> for a host with no storage account to lease against: always grants,
/// and says so once per acquisition at Warning so nobody mistakes a single-node dev host for a
/// coordinated one. Registered when neither <c>Storage:AccountName</c>/<c>Storage:ConnectionString</c>
/// nor the host's own <c>AzureWebJobsStorage</c> resolves to a blob endpoint — a bare test host, or a
/// `func start` without Azurite.
/// </summary>
/// <remarks>
/// Safe because the reset is idempotent and the Timer trigger is already singleton across instances;
/// see <see cref="IJobLock"/>. In Azure this is never the registered implementation — the Functions
/// host cannot start without its storage account at all.
/// </remarks>
public sealed class NullJobLock(ILogger<NullJobLock> logger) : IJobLock
{
    /// <inheritdoc />
    public Task<IJobLockHandle> TryAcquireAsync(string lockName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        logger.LogWarning(
            "No storage account is configured for the {LockName} lock, so this run is not coordinated with any other replica. Fine for a single local host; in Azure, check Storage:AccountName / AzureWebJobsStorage__accountName.",
            lockName);

        return Task.FromResult<IJobLockHandle>(Granted.Instance);
    }

    private sealed class Granted : IJobLockHandle
    {
        public static readonly Granted Instance = new();

        public bool IsAcquired => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
