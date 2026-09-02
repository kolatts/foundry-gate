using FoundryGate.Core.Quota;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Requests.Contracts;

namespace FoundryGate.Api.Services.Requests;

/// <summary>
/// The <c>/api/v1/requests</c> surface (spec &#167;4.4; issues #34, #35): a developer asks to move up
/// a budget tier, an admin approves or rejects, and an approval takes effect immediately.
/// </summary>
/// <remarks>
/// <para>
/// <b>A request asks for a tier, not a number (D-013).</b> <c>RequestedQuota</c> is either
/// <see langword="null"/> (unlimited) or exactly one of the configured tier caps — anything else is a
/// 400 listing the allowed values, because APIM's <c>token-quota</c> is a per-product literal and a
/// budget the gateway cannot enforce is not a budget. It must also be a genuine <em>increase</em> over
/// the caller's currently resolved quota; a developer who is already unlimited has nothing to ask for.
/// </para>
/// <para>
/// <b>One open request per user per period.</b> A second submission while one is still
/// <see cref="Domain.Requests.QuotaRequestStatusType.Pending"/> in the same billing period is a 409 —
/// the reviewer queue should not fill with the same person asking twice. A decided request frees the
/// slot immediately, so a rejected developer can re-ask with a better justification.
/// </para>
/// <para>
/// <b>Approval is the write path.</b> It sets the subject's <c>User.IsUnlimited</c> /
/// <c>User.MonthlyTokenQuota</c>, then re-runs <see cref="IQuotaResolutionService"/> for the
/// current period so the new budget (and the gateway tier behind it) is live before the response is
/// written — no cron job, no lag. Every mutation commits with its audit row.
/// </para>
/// <para>
/// <b>Both quota rules are re-checked at approval, against live resolution.</b> A stored
/// <c>RequestedQuota</c> is only applied if it is <em>still</em> a configured tier and <em>still</em> an
/// increase over what <see cref="IQuotaResolutionService.PreviewAsync"/> says the subject's budget
/// is now — otherwise approving a request filed before an admin raised them (or before a group did) would
/// silently lower it. Both submission and approval measure against that live answer rather than the
/// stored <c>QuotaAllocation</c> row, which only reflects the last resolution.
/// </para>
/// <para>
/// <b>A request only applies to the period it was filed for.</b> Approval re-resolves the
/// <em>current</em> period, so approving a July request in September would raise September's budget
/// while the row and the response still said July — the number in the UI and the month actually
/// affected disagreeing. Approving a request from a closed period is therefore a 409 naming that
/// period; rejecting one is always allowed, so a queue can be cleared by hand. The monthly reset
/// closes them itself (<see cref="Core.Requests.IQuotaRequestExpiry"/>), so in practice an admin never
/// meets one (#159).
/// </para>
/// <para>
/// <b>Reviews claim the row.</b> The transition out of <c>Pending</c> is a conditional update, so a
/// simultaneous approve and reject cannot both proceed: one wins, the other gets a 409. Everything a
/// review writes — the row, the subject's quota, the allocation, the audit entry — commits in one
/// transaction, and the service joins an ambient transaction rather than opening its own when an
/// orchestrator already has one.
/// </para>
/// </remarks>
public interface IQuotaRequestService
{
    /// <summary>
    /// Submits a request for the calling developer (<c>POST /requests</c>). Captures their live resolved
    /// quota, the current billing period, and <c>RequestedByUserId = </c> themselves. Writes nothing but
    /// the request and its audit row — no allocation is created, and no gateway call is made, so a
    /// refusal leaves no trace.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (→ 403; call <c>GET /users/me</c> first), or their account is deactivated (→ 403).</exception>
    /// <exception cref="ArgumentException"><c>RequestedQuota</c> is not a configured tier cap, or is not an increase over the caller's current quota (→ 400).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">
    /// The caller already has a pending request for this period (→ 409). Backed by the filtered unique
    /// index <c>IX_QuotaIncreaseRequests_PendingPerUserPeriod</c> (#147), so two concurrent submissions
    /// get the same answer as two sequential ones — exactly one row survives.
    /// </exception>
    Task<QuotaIncreaseRequestResponse> SubmitAsync(SubmitQuotaIncreaseRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Admin: submits a request on another user's behalf (<c>POST /requests/for/{userId}</c>) — the
    /// same rules evaluated against <paramref name="userId"/>, with <c>RequestedByUserId = </c> the
    /// calling admin so the trail shows who actually filed it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such user (→ 404).</exception>
    /// <exception cref="ArgumentException">As <see cref="SubmitAsync"/>, evaluated for the subject user (→ 400).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">
    /// The subject already has a pending request for this period, or is deactivated (→ 409). The
    /// duplicate case is constraint-backed; see <see cref="SubmitAsync"/>.
    /// </exception>
    Task<QuotaIncreaseRequestResponse> SubmitForUserAsync(int userId, SubmitQuotaIncreaseRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Lists requests newest first (<c>CreatedDate</c> then id, descending), paged. An admin sees
    /// every user's requests and may narrow with <see cref="QuotaRequestQuery.UserId"/>; anyone else
    /// sees only their own — naming another user in the filter is a 403, not a silently empty page.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (→ 403), or a non-admin filtered on another user's id (→ 403).</exception>
    Task<PagedResult<QuotaIncreaseRequestResponse>> ListAsync(QuotaRequestQuery filter, PagedRequest paging, CancellationToken cancellationToken);

    /// <summary>
    /// One request by id, for its subject or any admin.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (→ 403).</exception>
    /// <exception cref="KeyNotFoundException">
    /// No such request — <em>or</em> it belongs to someone else and the caller is not an admin (→ 404
    /// either way, deliberately: a 403 on an id that exists and a 404 on one that doesn't would let
    /// anyone enumerate other people's requests).
    /// </exception>
    Task<QuotaIncreaseRequestResponse> GetAsync(int quotaIncreaseRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// Admin: approves a pending request. Applies <c>RequestedQuota</c> to the subject user
    /// (<c>IsUnlimited = true</c> for an unlimited request, otherwise <c>MonthlyTokenQuota</c> with the
    /// flag cleared), re-resolves the current period's allocation — which moves the subject's APIM
    /// subscription to the new tier product — and writes <c>quota.approved</c> with the before/after
    /// quota. Request, user, allocation and audit row all commit in one <c>SaveChangesAsync</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such request (→ 404).</exception>
    /// <exception cref="ArgumentException">The stored <c>RequestedQuota</c> is no longer a configured tier cap — the tier table changed after submission (→ 400).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">
    /// The request is already approved or rejected (including by a reviewer racing this one), it was
    /// filed for a billing period that has since closed (→ 409; see the type remarks — reject it
    /// instead), the subject user is deactivated, or the stored <c>RequestedQuota</c> is no longer an
    /// increase over the subject's live quota — approving would lower it (→ 409).
    /// </exception>
    Task<QuotaIncreaseRequestResponse> ApproveAsync(int quotaIncreaseRequestId, ReviewQuotaIncreaseRequest review, CancellationToken cancellationToken);

    /// <summary>
    /// Admin: rejects a pending request with optional notes. Nothing about the subject's quota
    /// changes; the slot for a new request is freed. Audited as <c>quota.rejected</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such request (→ 404).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">The request is already approved or rejected (→ 409).</exception>
    Task<QuotaIncreaseRequestResponse> RejectAsync(int quotaIncreaseRequestId, ReviewQuotaIncreaseRequest review, CancellationToken cancellationToken);

    /// <summary>
    /// Closes every pending request belonging to <paramref name="userId"/> — the deprovisioning path's
    /// hook (#65/#66): an offboarded developer must not leave a request sitting in an admin's queue.
    /// Marks them <see cref="Domain.Requests.QuotaRequestStatusType.Rejected"/> with
    /// <paramref name="note"/> as the review notes and no reviewer (no human decided them), and
    /// returns how many were closed.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here saves and nothing here audits</b> — both belong to the calling orchestrator, so
    /// the cancellations commit atomically inside <em>its</em> unit of work and are described by
    /// <em>its</em> audit row (a lifecycle action, not a review decision). Idempotent: a second call
    /// finds nothing pending and returns 0. An orchestrator holding its own transaction can call any
    /// method on this service inside it; none of them opens a second one.
    /// </remarks>
    Task<int> CancelPendingForUserAsync(int userId, string note, CancellationToken cancellationToken);

    /// <summary>
    /// Closes every request left <c>Pending</c> from a billing period earlier than the current one, as
    /// <c>Rejected</c> with a system note and no reviewer, and audits the sweep as one
    /// <c>quota.requests-expired</c> row carrying the count. Saves.
    /// </summary>
    /// <remarks>
    /// The rule itself is <see cref="Core.Requests.IQuotaRequestExpiry"/> in Core, shared with the
    /// monthly reset — a timer and a button must not disagree about what expiry means. This entry point
    /// exists so the Api can sweep on its own (an operator tool, a future admin route); nothing over
    /// HTTP calls it yet, and the reset is what runs it on the normal schedule. Nothing external is
    /// touched, so the caller's own token applies throughout.
    /// </remarks>
    /// <returns>How many requests were closed; <c>0</c> writes nothing at all.</returns>
    Task<int> ExpireStaleAsync(CancellationToken cancellationToken);
}
