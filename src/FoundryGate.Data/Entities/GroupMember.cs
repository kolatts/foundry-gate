using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoundryGate.Data.Entities;

/// <summary>
/// A user's membership in a group. Composite key (no surrogate identity) — spec calls this out
/// explicitly and issue #22 lists it as the one deliberately composite-keyed entity.
/// </summary>
[PrimaryKey(nameof(GroupId), nameof(UserId))]
public class GroupMember
{
    public int GroupId { get; set; }

    public int UserId { get; set; }

    public DateTimeOffset AddedDate { get; set; }

    /// <summary>
    /// Admin who added this membership; <see langword="null"/> when the membership came from
    /// Entra group sync rather than an explicit admin action.
    /// </summary>
    public int? AddedByUserId { get; set; }

    // Navigation
    public Group Group { get; set; } = null!;

    public User User { get; set; } = null!;

    public User? AddedByUser { get; set; }
}

/// <summary>
/// Configures the two FKs to <see cref="User"/> explicitly (EF can't infer which
/// navigation pairs with which FK when a type has more than one relationship to the same
/// principal) and the cascade policy: <see cref="Group"/> owns the row (cascade delete
/// on group removal), everything else is <see cref="DeleteBehavior.NoAction"/> — at most one
/// cascade path per entity (CONVENTIONS.md).
/// </summary>
internal sealed class GroupMemberConfiguration : IEntityTypeConfiguration<GroupMember>
{
    public void Configure(EntityTypeBuilder<GroupMember> builder)
    {
        builder.HasOne(gm => gm.Group)
            .WithMany(g => g.GroupMemberships)
            .HasForeignKey(gm => gm.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(gm => gm.User)
            .WithMany(u => u.GroupMemberships)
            .HasForeignKey(gm => gm.UserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(gm => gm.AddedByUser)
            .WithMany()
            .HasForeignKey(gm => gm.AddedByUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
