using FoundryGate.Api.Services.Requests;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Requests.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/requests</c> (spec &#167;4.4; issues #34, #35) — the quota increase workflow. Submitting
/// and reading are open to every provisioned developer (scoped to their own requests); filing on
/// someone else's behalf and the two review actions are admin-only, declared per action.
/// </summary>
/// <remarks>
/// <para>
/// Spec &#167;4.4 writes the review actions as <c>PUT</c>; they are <c>POST</c> here. They are
/// non-idempotent state transitions with a body that is not the resource — the same shape as
/// <c>POST /users/{id}/activate</c>, <c>POST /keys/{userId}/rotate</c> and <c>POST /quota/reset</c>
/// elsewhere in this API — and re-sending one is a 409, not a no-op.
/// </para>
/// <para>
/// A request asks for a <em>tier</em>, not a number (D-013): <c>requestedQuota</c> is null (unlimited)
/// or one of <c>GET /quota/tiers</c>' caps. Rules and their status codes live in
/// <see cref="IQuotaRequestService"/>; errors arrive as ProblemDetails via
/// <c>GlobalExceptionHandler</c>.
/// </para>
/// </remarks>
public sealed class RequestsController(IQuotaRequestService quotaRequests) : ApiControllerBase
{
    /// <summary>Route name for the single-request GET, used to build the <c>Location</c> header on submit.</summary>
    public const string GetRequestRouteName = "GetQuotaIncreaseRequest";

    /// <summary>
    /// Lists quota increase requests newest first, paged. An admin sees every user's and may narrow
    /// with <c>?userId=</c>; anyone else sees only their own, and naming another user is a 403.
    /// <c>?status=</c> (<c>0</c> Pending, <c>1</c> Approved, <c>2</c> Rejected) filters both views.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<QuotaIncreaseRequestResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public Task<PagedResult<QuotaIncreaseRequestResponse>> ListAsync(
        [FromQuery] QuotaRequestQuery filter,
        [FromQuery] PagedRequest paging,
        CancellationToken cancellationToken) =>
        quotaRequests.ListAsync(filter, paging, cancellationToken);

    /// <summary>
    /// One request, for its subject or any admin. A request belonging to someone else is a <c>404</c>
    /// for a non-admin, exactly like an id that does not exist — the route is not an enumeration oracle.
    /// </summary>
    [HttpGet("{id:int}", Name = GetRequestRouteName)]
    [ProducesResponseType<QuotaIncreaseRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<QuotaIncreaseRequestResponse> GetAsync(int id, CancellationToken cancellationToken) =>
        quotaRequests.GetAsync(id, cancellationToken);

    /// <summary>
    /// Submits the caller's own request. <c>201</c> with a <c>Location</c> pointing at
    /// <see cref="GetAsync"/>. <c>400</c> when <c>requestedQuota</c> is not a configured tier cap or is
    /// not an increase over the caller's current budget; <c>409</c> when they already have a pending
    /// request this period; <c>403</c> when they have no user row yet or are deactivated.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<QuotaIncreaseRequestResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<QuotaIncreaseRequestResponse>> SubmitAsync(
        [FromBody] SubmitQuotaIncreaseRequest request,
        CancellationToken cancellationToken)
    {
        var created = await quotaRequests.SubmitAsync(request, cancellationToken);

        return CreatedAtRoute(GetRequestRouteName, new { id = created.QuotaIncreaseRequestId }, created);
    }

    /// <summary>
    /// Admin: submits a request on <paramref name="userId"/>'s behalf — same rules, evaluated for that
    /// user, with the calling admin recorded as <c>requestedByUserId</c>. <c>404</c> unknown user;
    /// <c>409</c> deactivated user or an existing pending request.
    /// </summary>
    [HttpPost("for/{userId:int}")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<QuotaIncreaseRequestResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<QuotaIncreaseRequestResponse>> SubmitForUserAsync(
        int userId,
        [FromBody] SubmitQuotaIncreaseRequest request,
        CancellationToken cancellationToken)
    {
        var created = await quotaRequests.SubmitForUserAsync(userId, request, cancellationToken);

        return CreatedAtRoute(GetRequestRouteName, new { id = created.QuotaIncreaseRequestId }, created);
    }

    /// <summary>
    /// Admin: approves a pending request. Applies the requested budget to the user and re-resolves
    /// their current-period allocation (moving their gateway tier) before responding. Send <c>{}</c>
    /// when there are no notes. <c>409</c> if the request was already decided or the user is
    /// deactivated; <c>400</c> if the requested value is no longer a configured tier.
    /// </summary>
    [HttpPost("{id:int}/approve")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<QuotaIncreaseRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<QuotaIncreaseRequestResponse> ApproveAsync(
        int id,
        [FromBody] ReviewQuotaIncreaseRequest review,
        CancellationToken cancellationToken) =>
        quotaRequests.ApproveAsync(id, review, cancellationToken);

    /// <summary>
    /// Admin: rejects a pending request with optional notes (send <c>{}</c> for none). Nothing about
    /// the user's quota changes, and the slot for a new request is freed. <c>409</c> if already decided.
    /// </summary>
    [HttpPost("{id:int}/reject")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<QuotaIncreaseRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<QuotaIncreaseRequestResponse> RejectAsync(
        int id,
        [FromBody] ReviewQuotaIncreaseRequest review,
        CancellationToken cancellationToken) =>
        quotaRequests.RejectAsync(id, review, cancellationToken);
}
