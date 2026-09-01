using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoundryGate.Data.Entities;

/// <summary>
/// An immutable record of an admin/system action (e.g. <c>"quota.approved"</c>, <c>"key.rotated"</c>).
/// </summary>
public class AuditLog
{
    public int AuditLogId { get; set; }

    /// <summary><see langword="null"/> for system-initiated actions with no human actor.</summary>
    public int? ActorUserId { get; set; }

    [Required]
    [StringLength(100)]
    public string Action { get; set; } = string.Empty;

    /// <summary>e.g. <c>"User"</c>, <c>"Group"</c>, <c>"Request"</c>; empty for actions with no single target.</summary>
    [StringLength(50)]
    public string TargetType { get; set; } = string.Empty;

    [StringLength(100)]
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Free-form JSON blob with action-specific detail.</summary>
    [StringLength(4000)]
    public string Details { get; set; } = string.Empty;

    public DateTimeOffset OccurredDate { get; set; }

    // Navigation
    public User? ActorUser { get; set; }
}

/// <summary>
/// Audit history must survive the actor being deleted, so this stays
/// <see cref="DeleteBehavior.NoAction"/> rather than cascading (CONVENTIONS.md: at most one
/// cascade path per entity, and this entity has none).
/// </summary>
internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.HasOne(a => a.ActorUser)
            .WithMany()
            .HasForeignKey(a => a.ActorUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
