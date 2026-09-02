using System.ComponentModel.DataAnnotations;
using FoundryGate.Data.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Data.Entities;

/// <summary>
/// A developer known to this fork of Foundry Gate, provisioned from Entra ID.
/// </summary>
/// <remarks>
/// The two non-unique indexes serve <c>GET /users</c> (CONVENTIONS.md: "Index the columns your default
/// ordering and filters use"). <c>DisplayName</c> alone covers the unfiltered list, which orders by it;
/// <c>(IsActive, DisplayName)</c> covers the far more common <c>?isActive=</c> view, giving the filter
/// and the ordering from one seek. <c>?search=</c> translates to <c>LIKE '%term%'</c>, which no B-tree
/// can seek — it scans, and on a fork-sized user table that is the right trade rather than a full-text
/// index nobody would maintain.
/// </remarks>
[Index(nameof(EntraObjectId), IsUnique = true)]
[Index(nameof(UserUnique), IsUnique = true)]
[Index(nameof(DisplayName))]
[Index(nameof(IsActive), nameof(DisplayName))]
public class User : ICreatedDate
{
    public int UserId { get; set; }

    /// <summary>
    /// Stable, non-sequential id for external systems that should not learn the identity-generated
    /// <see cref="UserId"/> (CONVENTIONS.md <c>{Entity}Unique</c> rule). Not used for the APIM
    /// subscription name — that is <c>foundrygate-{UserId}</c> (<c>Domain.Keys.ApimSubscriptionNames</c>),
    /// because plan 21 names it so and the usage-reconciliation job needs the trivial inverse mapping;
    /// the int is already exposed in every admin URL. Nothing names anything after this Guid yet.
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

    /// <summary>
    /// ARM resource id of this developer's APIM subscription
    /// (<c>.../Microsoft.ApiManagement/service/{apim}/subscriptions/foundrygate-{UserId}</c>); empty
    /// when no key is provisioned. Not a secret — it is an address, not a credential — so it is
    /// stored in the clear (#95).
    /// </summary>
    [StringLength(500)]
    public string ApimSubscriptionId { get; set; } = string.Empty;

    /// <summary>
    /// The APIM primary subscription key, <b>encrypted</b> by the Api's <c>IKeyProtector</c> before it
    /// is written (#95): a versioned envelope, <c>kv1:{keyVaultKeyId}:{base64}</c> for the Key Vault
    /// RSA-OAEP-256 wrap used in cloud environments or <c>dp1:{payload}</c> for the local-only
    /// Data Protection provider. Plaintext never touches this column. Sized for an RSA-4096 wrap
    /// (684 base64 chars) plus a versioned Key Vault key id (~110) with headroom. Empty when no key
    /// is provisioned.
    /// </summary>
    [StringLength(1000)]
    public string ApimSubscriptionKey { get; set; } = string.Empty;

    /// <summary>
    /// The last four characters of the plaintext key, kept so <c>GET /keys/me</c> can show the
    /// masked form (<c>••••••••1a2b</c>) without a Key Vault unwrap on every profile read. Four
    /// characters of a 32-character key is exactly the disclosure the masked display makes by
    /// design. Empty when no key is provisioned.
    /// </summary>
    [StringLength(4)]
    public string ApimSubscriptionKeyHint { get; set; } = string.Empty;

    /// <summary>When the current key value was minted (provisioning) or last regenerated (rotation); <see langword="null"/> when no key is provisioned.</summary>
    public DateTimeOffset? ApimKeyIssuedDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    public DateTimeOffset? LastSyncedDate { get; set; }

    // Navigation
    public ICollection<GroupMember> GroupMemberships { get; set; } = [];
}
