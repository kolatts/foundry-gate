using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using FoundryGate.Functions.Configuration;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Functions.Services.Quota;

/// <summary>
/// <see cref="IResetLock"/> over a blob lease on the Functions storage account — the standard Azure
/// primitive for "one replica at a time", and one FoundryGate already has the account and the
/// identity-based access for (CONVENTIONS.md §Storage accounts: the host reaches storage with its
/// user-assigned identity; shared-key access is off).
/// </summary>
/// <remarks>
/// <para>
/// A lease is taken for <see cref="StorageOptions.LockLeaseSeconds"/> and released on dispose. The
/// duration is a ceiling, not a deadline: if the process dies mid-reset the lease expires by itself,
/// so no manual unlock is ever needed — which is exactly why a lease beats a "lock row" here.
/// </para>
/// <para>
/// Every storage failure is swallowed into "not acquired" plus a log line, per
/// <see cref="IResetLock"/>: the job must degrade to "someone else has it" rather than throwing, and
/// the container is created on demand so a fresh fork needs no setup step.
/// </para>
/// </remarks>
public sealed class BlobResetLock(BlobServiceClient blobService, StorageOptions options, ILogger<BlobResetLock> logger) : IResetLock
{
    /// <inheritdoc />
    public async Task<IResetLockHandle> TryAcquireAsync(string lockName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        try
        {
            var container = blobService.GetBlobContainerClient(options.LockContainerName);
            _ = await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var blob = container.GetBlobClient($"{lockName}.lock");

            // The lease needs a blob to lease. Content is irrelevant — an existing blob from a previous
            // month is reused as-is, which is why the conflict is ignored rather than treated as failure.
            try
            {
                _ = await blob.UploadAsync(BinaryData.FromString(lockName), overwrite: false, cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status == 409)
            {
                // Already there (created by an earlier run, or by the replica we are racing).
            }

            var lease = blob.GetBlobLeaseClient();
            _ = await lease.AcquireAsync(TimeSpan.FromSeconds(options.LockLeaseSeconds), cancellationToken: cancellationToken);

            logger.LogDebug("Acquired the {LockName} lease for {LeaseSeconds}s.", lockName, options.LockLeaseSeconds);
            return new LeaseHandle(lease, lockName, logger);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            logger.LogInformation("Another replica holds the {LockName} lease; skipping this run.", lockName);
            return NotAcquired.Instance;
        }
        catch (RequestFailedException exception)
        {
            logger.LogWarning(
                exception,
                "Could not take the {LockName} lease on storage container {Container} ({Status}); skipping this run rather than risking a concurrent one. The next tick retries.",
                lockName,
                options.LockContainerName,
                exception.Status);
            return NotAcquired.Instance;
        }
    }

    private sealed class LeaseHandle(BlobLeaseClient lease, string lockName, ILogger logger) : IResetLockHandle
    {
        public bool IsAcquired => true;

        public async ValueTask DisposeAsync()
        {
            try
            {
                // CancellationToken.None: releasing is cleanup. A cancelled host must not leave the
                // lease held for its full duration when it could have handed it back immediately.
                _ = await lease.ReleaseAsync(cancellationToken: CancellationToken.None);
            }
            catch (RequestFailedException exception)
            {
                // Harmless: the lease expires on its own. Logged so a pattern of these is visible.
                logger.LogInformation(exception, "Could not release the {LockName} lease; it will expire on its own.", lockName);
            }
        }
    }

    private sealed class NotAcquired : IResetLockHandle
    {
        public static readonly NotAcquired Instance = new();

        public bool IsAcquired => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
