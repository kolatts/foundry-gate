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
/// <b>The lease is renewed, not just taken.</b> Azure caps a fixed-duration lease at 60 seconds, so a
/// fork large enough for the reset to run longer than that would lose its mutual exclusion mid-run —
/// and the release on dispose would fail too, because the lease id no longer matches. The handle
/// therefore renews in the background at a third of the duration for as long as it is held. A renewal
/// that fails is logged at Warning and the loop stops: the lease then expires on its own, which is the
/// same state as a crashed replica and is what makes the whole thing recoverable without a manual
/// unlock.
/// </para>
/// <para>
/// Every storage failure is swallowed into "not acquired" plus a log line, per
/// <see cref="IResetLock"/> — including credential failures, which are exactly what a not-yet-propagated
/// role assignment or an expired federated token looks like. The container is created on demand so a
/// fresh fork needs no setup step.
/// </para>
/// </remarks>
public sealed class BlobResetLock(BlobServiceClient blobService, StorageOptions options, ILogger<BlobResetLock> logger) : IResetLock
{
    /// <inheritdoc />
    public async Task<IResetLockHandle> TryAcquireAsync(string lockName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        var leaseDuration = TimeSpan.FromSeconds(options.LockLeaseSeconds);

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
            _ = await lease.AcquireAsync(leaseDuration, cancellationToken: cancellationToken);

            logger.LogDebug("Acquired the {LockName} lease for {LeaseSeconds}s, renewing while held.", lockName, options.LockLeaseSeconds);
            return new LeaseHandle(lease, lockName, leaseDuration, logger);
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Credential failures (an expired federated token, a role assignment that has not propagated
            // yet) surface as AuthenticationFailedException, not RequestFailedException, and would
            // otherwise escape and fail the whole reset — the precise outcome IResetLock's
            // "it does not throw" contract exists to prevent. Error, not Warning: unlike a 409 this is
            // something an operator has to fix. Cancellation is the host shutting us down, and must
            // still propagate.
            logger.LogError(
                exception,
                "Unexpected failure taking the {LockName} lease on storage container {Container}; skipping this run. Check the Functions identity's Storage Blob Data Owner assignment on the account.",
                lockName,
                options.LockContainerName);

            return NotAcquired.Instance;
        }
    }

    private sealed class LeaseHandle : IResetLockHandle
    {
        private readonly BlobLeaseClient _lease;
        private readonly string _lockName;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _renewals = new();
        private readonly Task _renewalLoop;

        public LeaseHandle(BlobLeaseClient lease, string lockName, TimeSpan leaseDuration, ILogger logger)
        {
            _lease = lease;
            _lockName = lockName;
            _logger = logger;

            // A third of the duration leaves room for two consecutive failures before the lease lapses.
            _renewalLoop = RenewAsync(leaseDuration / 3, _renewals.Token);
        }

        public bool IsAcquired => true;

        public async ValueTask DisposeAsync()
        {
            await _renewals.CancelAsync();

            try
            {
                await _renewalLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected: that is how the loop ends.
            }

            try
            {
                // CancellationToken.None: releasing is cleanup. A cancelled host must not leave the
                // lease held for its full duration when it could have handed it back immediately.
                _ = await _lease.ReleaseAsync(cancellationToken: CancellationToken.None);
            }
            catch (RequestFailedException exception)
            {
                // Harmless: the lease expires on its own. Logged so a pattern of these is visible.
                _logger.LogInformation(exception, "Could not release the {LockName} lease; it will expire on its own.", _lockName);
            }

            _renewals.Dispose();
        }

        private async Task RenewAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    _ = await _lease.RenewAsync(cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Stop renewing and let the lease lapse. Carrying on would just log the same failure
                    // every few seconds; the run continues, and the worst case is the one a crashed
                    // replica already produces — another instance may pick the reset up, idempotently.
                    _logger.LogWarning(exception, "Could not renew the {LockName} lease; it will lapse and another replica may take it.", _lockName);
                    return;
                }
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
