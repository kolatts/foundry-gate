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

/// <summary>POST /requests body — a developer (or admin on their behalf) submits a quota increase request.</summary>
/// <param name="RequestedQuota">Null means requesting unlimited (spec &#167;3.1).</param>
/// <param name="Justification">Free-text reason for the request; required so reviewers have something to evaluate.</param>
public record SubmitQuotaIncreaseRequest(
    [property: Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    long? RequestedQuota,
    [property: Required, StringLength(ValidationConstants.JustificationMaxLength, MinimumLength = ValidationConstants.JustificationMinLength)]
    string Justification);

/// <summary>PUT /requests/{id}/approve or PUT /requests/{id}/reject body — same shape for both (spec &#167;4.4).</summary>
public record ReviewQuotaIncreaseRequest(
    [property: StringLength(ValidationConstants.ReviewNotesMaxLength)]
    string? ReviewNotes);
