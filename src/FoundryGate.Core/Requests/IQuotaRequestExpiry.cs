using FoundryGate.Domain.Quota;

namespace FoundryGate.Core.Requests;

/// <summary>
/// Closes quota increase requests that outlived the billing period they were filed for (#159).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is in Core.</b> Two hosts have to do the same thing to the same rows: the monthly
/// reset — which runs both from the Functions timer and from an admin's <c>POST /quota/reset</c>, via
/// <see cref="Quota.IQuotaResetService"/> — and the Api's own
/// <c>IQuotaRequestService.ExpireStaleAsync</c>. Core cannot reference the Api, and "what expiry means"
/// must not fork between a timer and a button, so the rule lives here and both hosts call it
/// (CONVENTIONS.md &#167;Solution structure).
/// </para>
/// <para>
/// <b>What it does.</b> Every <c>Pending</c> request whose <c>(PeriodYear, PeriodMonth)</c> is
/// <em>earlier</em> than the period it is given becomes <c>Rejected</c>, stamped with the run's instant
/// and a system review note — the same shape
/// <c>QuotaRequestService.CancelPendingForUserAsync</c> uses for a departing user's requests, because
/// the status enum has no "Expired" state and no human decided these. <c>ReviewedByUserId</c> stays
/// <see langword="null"/>, which is what distinguishes a lapsed request from a rejected one in the trail.
/// A request filed for the current period is left alone, and so is one for a <em>later</em> period
/// (nothing produces those today; expiring a request early would be worse than ignoring it).
/// </para>
/// <para>
/// <b>Nothing here saves</b> (CONVENTIONS.md audit pattern): the rows and the single
/// <c>quota.requests-expired</c> audit row are added to the caller's <c>AppDbContext</c> and commit
/// with whatever else that unit of work is doing — for the reset, the allocations it just re-resolved.
/// </para>
/// </remarks>
public interface IQuotaRequestExpiry
{
    /// <summary>
    /// Marks every pending request from a closed period as <c>Rejected</c> and adds one
    /// <c>quota.requests-expired</c> audit row carrying the count. Returns how many were closed;
    /// <c>0</c> writes no audit row, so an ordinary reset does not leave a row a month saying nothing
    /// happened.
    /// </summary>
    /// <param name="current">The period that is still open — everything strictly before it expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> ExpireStaleAsync(BillingPeriod current, CancellationToken cancellationToken);
}
