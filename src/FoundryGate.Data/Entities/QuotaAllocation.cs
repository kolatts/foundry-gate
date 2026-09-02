using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoundryGate.Data.Entities;

/// <summary>
/// The resolved token quota for one user for one calendar month (spec §3.2 resolution output).
/// Enforcement itself happens in real time at the APIM gateway's <c>llm-token-limit</c> policy —
/// this row is what the monthly-reset Function writes after resolving quota tiers, and what
/// reconciliation compares consumption against; it is not read on the request hot path.
/// </summary>
/// <remarks>
/// The <c>(PeriodYear, PeriodMonth)</c> index backs <c>GET /quota/allocations</c>, whose every page
/// filters on the current period; the unique index leads with <c>UserId</c> so it cannot serve that
/// seek.
/// </remarks>
[Index(nameof(UserId), nameof(PeriodYear), nameof(PeriodMonth), IsUnique = true)]
[Index(nameof(PeriodYear), nameof(PeriodMonth))]
public class QuotaAllocation
{
    public int QuotaAllocationId { get; set; }

    public int UserId { get; set; }

    public int PeriodYear { get; set; }

    /// <summary>1-12.</summary>
    public int PeriodMonth { get; set; }

    /// <summary><see langword="null"/> means unlimited for this period.</summary>
    public long? AllocatedTokens { get; set; }

    /// <summary>
    /// Reconciliation state only — populated from the <c>ApiManagementGatewayLlmLog</c> Log
    /// Analytics sync (issue #10 direction update), never from a request-time write. The gateway
    /// enforces the monthly quota itself via APIM's <c>llm-token-limit</c> policy; this column
    /// exists so operators/admins can see actual usage against the allocation, not to gate access.
    /// </summary>
    public long TokensUsed { get; set; }

    /// <summary>
    /// Deactivation/offboarding suspension only — quota exhaustion is enforced and 403'd by APIM
    /// directly and does not set this flag (issue #7 direction update).
    /// </summary>
    public bool IsHardStopped { get; set; }

    /// <summary>Which level of the five-level precedence chain produced <see cref="AllocatedTokens"/> (issue #32).</summary>
    public QuotaLevelType ResolvedLevelType { get; set; }

    /// <summary>
    /// The APIM tier product (<c>FoundryGate.Domain.Constants.GatewayTiers</c>) this budget is. A
    /// monthly budget <em>is</em> a tier (D-013): every quota the control plane accepts equals a
    /// configured tier cap or is unlimited, so <see cref="AllocatedTokens"/> and this tier's cap normally
    /// agree. The tier, not the number, is what the gateway enforces — <c>token-quota</c> is a
    /// per-product literal (#82) — so the developer's subscription is issued against this product.
    /// </summary>
    [Required]
    [StringLength(64)]
    public string TierProductId { get; set; } = string.Empty;

    /// <summary>
    /// True when <see cref="AllocatedTokens"/> did not match any configured tier cap (a legacy or
    /// hand-edited value) and is therefore enforced at the next tier up — or the largest finite tier —
    /// rather than at the number stored. Surfaced so admins can correct the value to a tier.
    /// </summary>
    public bool IsGatewayCapped { get; set; }

    /// <summary><see langword="null"/> until a monthly/manual reset first (re)resolves this period's row; a row created on demand (first <c>/me</c> of the month) has none.</summary>
    public DateTimeOffset? ResetDate { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}

/// <summary>Only FK on this entity, so it is free to be the entity's cascade path.</summary>
internal sealed class QuotaAllocationConfiguration : IEntityTypeConfiguration<QuotaAllocation>
{
    public void Configure(EntityTypeBuilder<QuotaAllocation> builder)
    {
        builder.HasOne(qa => qa.User)
            .WithMany()
            .HasForeignKey(qa => qa.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
