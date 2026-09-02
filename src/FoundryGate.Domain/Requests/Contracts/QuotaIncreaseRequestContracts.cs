using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Domain.Requests.Contracts;

/// <summary>A quota increase request (spec &#167;3.1 <c>QuotaIncreaseRequest</c>). GET /requests (paged), GET /requests/{id}.</summary>
public record QuotaIncreaseRequestResponse(
    int QuotaIncreaseRequestId,
    Guid QuotaIncreaseRequestUnique,
    int UserId,
    Guid UserUnique,
    string UserDisplayName,
    int RequestedByUserId,
    int PeriodYear,
    int PeriodMonth,
    long? CurrentQuota,
    long? RequestedQuota,
    string Justification,
    QuotaRequestStatusType StatusType,
    int? ReviewedByUserId,
    DateTimeOffset? ReviewedDate,
    string? ReviewNotes,
    DateTimeOffset CreatedDate);

/// <summary>
/// POST /requests body — a developer (or admin on their behalf) submits a quota increase request.
/// Init-property record, not positional — see <see cref="Foundry.Contracts.CreateFoundryDeploymentRequest"/>'s remarks (#128).
/// </summary>
public record SubmitQuotaIncreaseRequest
{
    /// <summary>Null means requesting unlimited (spec &#167;3.1).</summary>
    [Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    public long? RequestedQuota { get; init; }

    /// <summary>Free-text reason for the request; required so reviewers have something to evaluate.</summary>
    [Required]
    [StringLength(ValidationConstants.JustificationMaxLength, MinimumLength = ValidationConstants.JustificationMinLength)]
    public string Justification { get; init; } = string.Empty;
}

/// <summary>POST /requests/{id}/approve or POST /requests/{id}/reject body — same shape for both (spec &#167;4.4).</summary>
/// <remarks>
/// The body itself is required even when there are no notes (send <c>{}</c>): <c>[ApiController]</c>
/// turns a missing body into a 400 before the action runs, and keeping it mandatory is what lets
/// controller actions stay guard-free delegations (CONVENTIONS.md).
/// </remarks>
public record ReviewQuotaIncreaseRequest
{
    /// <summary>Optional reviewer notes, shown to the requester.</summary>
    [StringLength(ValidationConstants.ReviewNotesMaxLength)]
    public string? ReviewNotes { get; init; }
}

/// <summary>
/// Filter parameters for GET /requests. Bind alongside <see cref="Common.PagedRequest"/> via a
/// separate <c>[FromQuery]</c> parameter, exactly as <see cref="Audit.Contracts.AuditLogQuery"/> does.
/// </summary>
/// <param name="Status">Only requests in this review state; all states when <see langword="null"/>.</param>
/// <param name="UserId">
/// Only requests whose <em>subject</em> is this user. Admin-only in effect: a non-admin caller only
/// ever sees their own requests, and naming another user here is a 403 rather than a silently
/// empty page.
/// </param>
public record QuotaRequestQuery(QuotaRequestStatusType? Status, int? UserId);
