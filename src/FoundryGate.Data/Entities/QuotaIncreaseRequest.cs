using System.ComponentModel.DataAnnotations;
using FoundryGate.Data.Interfaces;
using FoundryGate.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoundryGate.Data.Entities;

/// <summary>
/// A developer- or admin-initiated request to raise a user's quota for a given period, subject to
/// admin review.
/// </summary>
/// <remarks>
/// Uses <see cref="QuotaRequestStatusType"/> (added by #91's
/// contracts) rather than a Data-local enum — the two PRs briefly defined the same concept
/// twice; this is the reconciled single source.
/// </remarks>
[Index(nameof(QuotaIncreaseRequestUnique), IsUnique = true)]

// GET /requests' two access shapes (CONVENTIONS.md: index what your default ordering and filters use;
// AuditLog.OccurredDate is the precedent). CreatedDate alone serves the admin's unfiltered list, which
// is ordered newest-first across every user and can narrow on nothing else. (UserId, StatusType) serves
// both the developer's own list — always filtered to one user, often to Pending — and the admin's
// ?userId=&status=; it leads with UserId so it also subsumes the FK index EF would otherwise create.
// Deliberately two indexes rather than one composite: no single column order serves an ordering that
// applies with no user filter and a filter that applies with no ordering advantage.
[Index(nameof(CreatedDate))]
[Index(nameof(UserId), nameof(StatusType))]

// "One PENDING request per user per period" (#147), as a constraint rather than only as
// QuotaRequestService's read-then-write check — two concurrent submissions (a double-clicked button, a
// retrying client) can both pass that check and both insert. Read this attribute together with
// QuotaIncreaseRequestConfiguration's HasFilter: the uniqueness applies ONLY to rows whose StatusType
// is Pending, which is what lets a user accumulate a decided request per period and file a new one
// after each decision. An unfiltered unique index here would let a developer be refused for the rest
// of the month by their own approved request.
[Index(nameof(UserId), nameof(PeriodYear), nameof(PeriodMonth), IsUnique = true, Name = QuotaIncreaseRequestConfiguration.PendingPerUserPeriodIndexName)]
public class QuotaIncreaseRequest : ICreatedDate
{
    public int QuotaIncreaseRequestId { get; set; }

    /// <summary>Stable id for links (e.g. a review-request email/notification deep link).</summary>
    public Guid QuotaIncreaseRequestUnique { get; set; } = Guid.NewGuid();

    public int UserId { get; set; }

    /// <summary>Who filed the request — the user themselves or an admin on their behalf.</summary>
    public int RequestedByUserId { get; set; }

    public int PeriodYear { get; set; }

    /// <summary>1-12.</summary>
    public int PeriodMonth { get; set; }

    public long? CurrentQuota { get; set; }

    /// <summary><see langword="null"/> means the request is for unlimited.</summary>
    public long? RequestedQuota { get; set; }

    [Required]
    [StringLength(2000)]
    public string Justification { get; set; } = string.Empty;

    public QuotaRequestStatusType StatusType { get; set; } = QuotaRequestStatusType.Pending;

    /// <summary><see langword="null"/> until an admin reviews the request.</summary>
    public int? ReviewedByUserId { get; set; }

    public DateTimeOffset? ReviewedDate { get; set; }

    [StringLength(2000)]
    public string ReviewNotes { get; set; } = string.Empty;

    public DateTimeOffset CreatedDate { get; set; }

    // Navigation
    public User User { get; set; } = null!;

    public User RequestedByUser { get; set; } = null!;

    public User? ReviewedByUser { get; set; }
}

/// <summary>
/// The filtered unique index's <c>WHERE</c> clause (an attribute cannot express one), plus the three
/// separate FKs into <see cref="User"/>. Only <see cref="QuotaIncreaseRequest.UserId"/>
/// is the entity's cascade path (deleting the subject user removes their requests); the requester
/// and reviewer links stay <see cref="DeleteBehavior.NoAction"/> so deleting an admin account never
/// cascades into unrelated users' request history.
/// </summary>
internal sealed class QuotaIncreaseRequestConfiguration : IEntityTypeConfiguration<QuotaIncreaseRequest>
{
    /// <summary>
    /// Name of the filtered unique index declared by <see cref="QuotaIncreaseRequest"/>'s
    /// <c>[Index]</c> attribute; referenced here so the filter lands on that index rather than
    /// creating a second, unnamed one.
    /// </summary>
    internal const string PendingPerUserPeriodIndexName = "IX_QuotaIncreaseRequests_PendingPerUserPeriod";

    /// <summary>
    /// The half of that index an attribute cannot express, written identically here and in
    /// <c>dbo/Tables/QuotaIncreaseRequests.sql</c> — <c>SchemaParityTests</c> compares the two as text.
    /// The literal is <see cref="QuotaRequestStatusType.Pending"/>'s stored <c>int</c>: an index filter
    /// is database text, so it cannot name the enum. Valid on SQLite too (partial indexes, and
    /// bracket-quoted identifiers), which is what lets the test harness create the same constraint the
    /// deployed database has.
    /// </summary>
    internal const string PendingStatusFilter = "[StatusType] = 0";

    public void Configure(EntityTypeBuilder<QuotaIncreaseRequest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder
            .HasIndex([nameof(QuotaIncreaseRequest.UserId), nameof(QuotaIncreaseRequest.PeriodYear), nameof(QuotaIncreaseRequest.PeriodMonth)], PendingPerUserPeriodIndexName)
            .HasFilter(PendingStatusFilter);

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.RequestedByUser)
            .WithMany()
            .HasForeignKey(r => r.RequestedByUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(r => r.ReviewedByUser)
            .WithMany()
            .HasForeignKey(r => r.ReviewedByUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
