using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoundryGate.Data.Entities;

/// <summary>
/// The resolved token quota for one user for one calendar month (spec §3.2 resolution output).
/// Enforcement itself happens in real time at the APIM gateway's <c>llm-token-limit</c> policy —
/// this row is what the monthly-reset Function writes after resolving quota tiers, and what
/// reconciliation compares consumption against; it is not read on the request hot path.
/// </summary>
[Index(nameof(UserId), nameof(PeriodYear), nameof(PeriodMonth), IsUnique = true)]
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

    /// <summary><see langword="null"/> until the monthly-reset Function first (re)creates this period's row.</summary>
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
