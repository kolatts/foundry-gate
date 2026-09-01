using System.ComponentModel.DataAnnotations;
using FoundryGate.Data.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Data.Entities;

/// <summary>
/// A developer known to this fork of Foundry Gate, provisioned from Entra ID.
/// </summary>
[Index(nameof(EntraObjectId), IsUnique = true)]
[Index(nameof(UserUnique), IsUnique = true)]
public class User : ICreatedDate
{
    public int UserId { get; set; }

    /// <summary>
    /// Stable id referenced by external systems that need to name resources after this user
    /// without exposing the identity/mutable <see cref="UserId"/> (e.g. the APIM subscription
    /// name minted for this developer).
    /// </summary>
    public Guid UserUnique { get; set; } = Guid.NewGuid();

    /// <summary>The Entra ID object id this user was provisioned from.</summary>
    [Required]
    [StringLength(64)]
    public string EntraObjectId { get; set; } = string.Empty;

    /// <summary>
    /// HR employee id from Entra, when available. Nullable (not just empty) because "no employee
    /// id on record" and "Entra hasn't synced this attribute yet" are both real, distinct states
    /// a caller may need to tell apart from "known to be blank".
    /// </summary>
    [StringLength(64)]
    public string? EmployeeId { get; set; }

    [Required]
    [StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    [StringLength(320)]
    public string Email { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>When set, this user's quota resolution short-circuits to unlimited (spec §3.2 step 1).</summary>
    public bool IsUnlimited { get; set; }

    /// <summary>Per-user override; <see langword="null"/> means "fall through to group/system default" (spec §3.2).</summary>
    public long? MonthlyTokenQuota { get; set; }

    [StringLength(500)]
    public string ApimSubscriptionId { get; set; } = string.Empty;

    /// <summary>Encrypted at rest (encryption is applied by the service layer, not this column).</summary>
    [StringLength(500)]
    public string ApimSubscriptionKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedDate { get; set; }

    public DateTimeOffset? LastSyncedDate { get; set; }

    // Navigation
    public ICollection<GroupMember> GroupMemberships { get; set; } = [];
}
