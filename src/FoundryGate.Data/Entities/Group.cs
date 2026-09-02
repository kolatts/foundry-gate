using System.ComponentModel.DataAnnotations;
using FoundryGate.Data.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoundryGate.Data.Entities;

/// <summary>
/// A group of users that can carry its own quota policy, optionally synced from an Entra group.
/// </summary>
[Index(nameof(GroupUnique), IsUnique = true)]
[Index(nameof(Name), IsUnique = true)]
public class Group : ICreatedDate
{
    public int GroupId { get; set; }

    /// <summary>
    /// Stable id referenced by external callers/UI (#91's <c>GroupResponse.GroupUnique</c>) —
    /// mirrors <see cref="User.UserUnique"/>.
    /// </summary>
    public Guid GroupUnique { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Display name, unique across the fork. The uniqueness is a database constraint rather than a
    /// service-level "does one already exist?" check alone, so two concurrent <c>POST /groups</c> for
    /// the same name cannot both win; it also serves the group list's default ordering and its
    /// <c>?search=</c> filter.
    /// </summary>
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Entra group object id this group mirrors; empty when the group is FoundryGate-native. Unique
    /// among the non-empty values (<see cref="GroupConfiguration"/>) — two groups backed by one
    /// directory group would both claim its members. "Is this group Entra-synced?" is this column
    /// being non-empty and is derived at read time (<c>GroupResponse.IsEntraSynced</c>), never stored
    /// separately: a second copy of the same fact can only drift from it.
    /// </summary>
    [StringLength(64)]
    public string EntraGroupId { get; set; } = string.Empty;

    /// <summary>Overrides the system default for members without their own <see cref="User.MonthlyTokenQuota"/> (spec §3.2).</summary>
    public long? MonthlyTokenQuota { get; set; }

    /// <summary>When set, members without their own override resolve to unlimited (spec §3.2 step 3).</summary>
    public bool IsUnlimited { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    // Navigation
    public ICollection<GroupMember> GroupMemberships { get; set; } = [];
}

/// <summary>
/// The one index an attribute cannot express: <see cref="Group.EntraGroupId"/> is unique only among
/// the groups that actually have a link. The column is non-nullable (CONVENTIONS.md: strings are
/// <c>string.Empty</c>, not null), so an unfiltered unique index would allow exactly one native group
/// in the whole fork.
/// </summary>
internal sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    /// <summary>The filter, written identically here and in <c>dbo/Tables/Groups.sql</c> — <c>SchemaParityTests</c> compares the two as text.</summary>
    internal const string EntraGroupIdFilter = "[EntraGroupId] <> ''";

    public void Configure(EntityTypeBuilder<Group> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.HasIndex(group => group.EntraGroupId)
            .IsUnique()
            .HasFilter(EntraGroupIdFilter);
    }
}
