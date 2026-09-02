using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Core.Requests;

/// <summary>
/// Default <see cref="IQuotaRequestExpiry"/>. Scoped: shares the caller's <see cref="AppDbContext"/>,
/// so the closed requests and the run's single audit row commit with the caller's own work. Semantics
/// are on the interface.
/// </summary>
public sealed class QuotaRequestExpiry(
    AppDbContext dbContext,
    IAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<QuotaRequestExpiry> logger) : IQuotaRequestExpiry
{
    /// <summary>
    /// The <c>ReviewNotes</c> a lapsed request carries. Written for the developer who comes back and
    /// asks why: it names the reason and the remedy, and the absent <c>ReviewedByUserId</c> says no
    /// admin turned this down. Public because it is user-visible text that ends up on the wire — a UI
    /// that wants to recognise it, and the tests that pin it, should compare against this rather than
    /// re-typing it.
    /// </summary>
    public const string SystemNote =
        "Closed automatically: the billing period this request was filed for has ended, so approving it would have raised the budget for a month that is already over. Submit a new request for the current period if the need stands.";

    /// <inheritdoc />
    public async Task<int> ExpireStaleAsync(BillingPeriod current, CancellationToken cancellationToken)
    {
        // Tracked, not ExecuteUpdate: the rows go into the caller's unit of work so they commit with the
        // reset's allocations (and roll back with them), and "how many, for whom" is needed for the
        // audit row either way. A fork's stale queue is single digits — this is not a bulk path.
        var stale = await dbContext.QuotaIncreaseRequests
            .Where(r => r.StatusType == QuotaRequestStatusType.Pending
                && (r.PeriodYear < current.Year || (r.PeriodYear == current.Year && r.PeriodMonth < current.Month)))
            .OrderBy(r => r.QuotaIncreaseRequestId)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var request in stale)
        {
            // Rejected with no ReviewedByUserId — the CancelPendingForUserAsync shape. The enum has no
            // "Expired" state, and adding one would be a schema change every read path would have to
            // learn; the null reviewer plus the note is what tells the two apart.
            request.StatusType = QuotaRequestStatusType.Rejected;
            request.ReviewedDate = now;
            request.ReviewNotes = SystemNote;
        }

        // One row for the run, not one per request: a fork that let six months of queue accumulate would
        // otherwise bury every other action in the audit viewer. No single target, so both target fields
        // are empty (CONVENTIONS.md). System actor: no human decided these.
        _ = audit.AddSystem(
            AuditActions.QuotaRequestsExpired,
            string.Empty,
            string.Empty,
            new
            {
                expiredCount = stale.Count,
                currentPeriodYear = current.Year,
                currentPeriodMonth = current.Month,
                expiredRequestIds = stale.Select(r => r.QuotaIncreaseRequestId).ToArray(),
            });

        logger.LogInformation(
            "Closed {ExpiredCount} quota increase request(s) left pending from a period earlier than {Period}.",
            stale.Count,
            current);

        return stale.Count;
    }

    /// <inheritdoc />
    public async Task<int> CancelPendingForUserAsync(int userId, string note, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);

        var pending = await dbContext.QuotaIncreaseRequests
            .Where(r => r.UserId == userId && r.StatusType == QuotaRequestStatusType.Pending)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        foreach (var request in pending)
        {
            // Rejected, with no ReviewedByUserId: the status enum has no "Cancelled" state and no
            // human decided these — the note says a lifecycle event closed them.
            request.StatusType = QuotaRequestStatusType.Rejected;
            request.ReviewedDate = now;
            request.ReviewNotes = note;
        }

        return pending.Count;
    }
}
