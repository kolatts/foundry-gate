using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoundryGate.Data.Entities;

/// <summary>
/// An immutable record of an admin/system action (e.g. <c>"quota.approved"</c>, <c>"key.rotated"</c>).
/// </summary>
/// <remarks>
/// <para>
/// Indexed on <see cref="OccurredDate"/> because every page of <c>GET /audit</c> orders by it
/// (newest first) and the date-range filter seeks on it — without the index an unfiltered admin
/// view is a full sort of the table forever. Ascending is fine for a descending scan; SQL Server
/// reads the index backwards. The FK index on <see cref="ActorUserId"/> is EF's implicit one.
/// </para>
/// <para>
/// The composite <c>(Action, TargetType, TargetId, OccurredDate)</c> index serves the reveal anomaly
/// signal (#180), which asks "how many <c>key.revealed</c> rows does this key have in the last hour?"
/// on every reveal. On the date index alone that is a range seek over <em>every</em> row written in
/// the window plus a key lookup each; leading with the three equality predicates makes it a seek to
/// one narrow range, and putting <see cref="OccurredDate"/> last covers the count without touching
/// the table (#211 review).
/// </para>
/// </remarks>
[Index(nameof(OccurredDate))]
[Index(nameof(Action), nameof(TargetType), nameof(TargetId), nameof(OccurredDate))]
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

    /// <summary>
    /// Free-form JSON blob with action-specific detail. Deliberately no <c>[StringLength]</c>,
    /// which is what leaves it unbounded (<c>nvarchar(max)</c> on SQL Server) — a fixed cap risks
    /// silently truncating exactly the payload an admin needs when auditing an incident, and
    /// there's no #91 <c>ValidationConstants</c> entry to align with since this column is never
    /// written through a validated request DTO. See <see cref="AuditLogConfiguration"/> for why
    /// this isn't instead spelled out via <c>HasColumnType("nvarchar(max)")</c>.
    /// </summary>
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

        // Details deliberately has no HasColumnType/StringLength override: leaving it unconfigured
        // is what makes EF Core map it to nvarchar(max) on SQL Server (and unbounded TEXT on
        // SQLite) — explicitly writing "nvarchar(max)" via HasColumnType is SQL-Server-only syntax
        // that SQLite's own DDL parser rejects (the length "max" isn't a number), which would
        // break the SQLite in-memory test harness.
    }
}
