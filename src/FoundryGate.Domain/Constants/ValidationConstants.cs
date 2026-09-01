namespace FoundryGate.Domain.Constants;

/// <summary>
/// Max-length and range constraints shared between API <c>DataAnnotations</c>
/// attributes and Blazor form validation, so the two never drift (issue #25).
/// </summary>
public static class ValidationConstants
{
    public const int DisplayNameMaxLength = 200;

    /// <summary>RFC 5321 &#167;4.5.3.1.3 maximum mailbox length.</summary>
    public const int EmailMaxLength = 320;

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
}
