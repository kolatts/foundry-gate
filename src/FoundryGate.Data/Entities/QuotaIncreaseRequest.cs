using System.ComponentModel.DataAnnotations;
using FoundryGate.Data.Interfaces;
using FoundryGate.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoundryGate.Data.Entities;

/// <summary>
/// A developer- or admin-initiated request to raise a user's quota for a given period, subject to
/// admin review.
/// </summary>
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
/// Three separate FKs into <see cref="User"/>. Only <see cref="QuotaIncreaseRequest.UserId"/>
/// is the entity's cascade path (deleting the subject user removes their requests); the requester
/// and reviewer links stay <see cref="DeleteBehavior.NoAction"/> so deleting an admin account never
/// cascades into unrelated users' request history.
/// </summary>
internal sealed class QuotaIncreaseRequestConfiguration : IEntityTypeConfiguration<QuotaIncreaseRequest>
{
    public void Configure(EntityTypeBuilder<QuotaIncreaseRequest> builder)
    {
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
