namespace FoundryGate.Domain.Requests;

/// <summary>
/// Review status of a <c>QuotaIncreaseRequest</c> (spec &#167;3.1). Stored as <c>int</c>
/// (CONVENTIONS.md: "Enums stored as int, property suffixed Type").
/// </summary>
public enum QuotaRequestStatusType
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}
