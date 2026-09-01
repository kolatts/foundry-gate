namespace FoundryGate.Domain.Constants;

/// <summary>
/// Max-length and range constraints shared between API <c>DataAnnotations</c>
/// attributes and Blazor form validation, so the two never drift (issue #25).
/// </summary>
public static class ValidationConstants
{
    public const int DisplayNameMaxLength = 200;

    /// <summary>
    /// A conservative upper bound, not an RFC-derived one: the commonly cited
    /// "64 (local-part, RFC 5321 &#167;4.5.3.1.1) + 1 ('@') + 255 (domain) = 320"
    /// folk sum. RFC 5321 &#167;4.5.3.1.3 actually caps the total reverse-/forward-path
    /// at 256 octets, which nets out to a 254-character deliverable address — 320 is
    /// deliberately looser than that so this bound never rejects a real address.
    /// </summary>
    public const int EmailMaxLength = 320;

    /// <summary>Entra object ids are GUIDs (36 chars); this leaves headroom without being unbounded. See <see cref="GuidPattern"/>.</summary>
    public const int EntraObjectIdMaxLength = 64;

    public const int EmployeeIdMaxLength = 64;

    public const int GroupNameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    public const int JustificationMinLength = 10;
    public const int JustificationMaxLength = 2000;
    public const int ReviewNotesMaxLength = 2000;

    public const int ConfigKeyMaxLength = 200;
    public const int ConfigValueMaxLength = 4000;

    public const int AuditActionMaxLength = 100;
    public const int AuditTargetTypeMaxLength = 50;
    public const int AuditTargetIdMaxLength = 100;

    /// <summary>
    /// Sanity ceiling for an admin-entered monthly token quota — not a business rule
    /// from the spec, just a guardrail against a fat-fingered value (e.g. an extra zero)
    /// reaching APIM. 100B tokens/month is far beyond any real allocation.
    /// </summary>
    public const long MaxMonthlyTokenQuota = 100_000_000_000;

    /// <summary>
    /// Canonical (hyphenated, no braces) GUID shape, for <c>[RegularExpression]</c> on
    /// GUID-shaped identifier strings such as <c>Group.EntraGroupId</c> — kept as a
    /// string rather than typing those fields <see cref="Guid"/> directly so they stay
    /// consistent with <c>User.EntraObjectId</c>, which the data layer also models as a
    /// string.
    /// </summary>
    public const string GuidPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
}
