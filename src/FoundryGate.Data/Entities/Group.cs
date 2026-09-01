using System.ComponentModel.DataAnnotations;
using FoundryGate.Data.Interfaces;

namespace FoundryGate.Data.Entities;

/// <summary>
/// A group of users that can carry its own quota policy, optionally synced from an Entra group.
/// </summary>
public class Group : ICreatedDate
{
    public int GroupId { get; set; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Entra group object id this group mirrors; empty when the group is FoundryGate-native.</summary>
    [StringLength(64)]
    public string EntraGroupId { get; set; } = string.Empty;

    public bool IsEntraSynced { get; set; }

    /// <summary>Overrides the system default for members without their own <see cref="User.MonthlyTokenQuota"/> (spec §3.2).</summary>
    public long? MonthlyTokenQuota { get; set; }

    /// <summary>When set, members without their own override resolve to unlimited (spec §3.2 step 3).</summary>
    public bool IsUnlimited { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    // Navigation
    public ICollection<GroupMember> GroupMemberships { get; set; } = [];
}
