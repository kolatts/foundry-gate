namespace FoundryGate.Domain.Enums;

/// <summary>
/// The review state of a <c>QuotaIncreaseRequest</c>.
/// </summary>
public enum QuotaRequestStatusType
{
    /// <summary>Awaiting admin review.</summary>
    Pending = 0,

    /// <summary>Approved by an admin; the requested quota takes effect.</summary>
    Approved = 1,

    /// <summary>Rejected by an admin; no change to the user's quota.</summary>
    Rejected = 2
}
