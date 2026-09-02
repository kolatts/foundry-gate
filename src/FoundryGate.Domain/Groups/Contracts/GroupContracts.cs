using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Domain.Groups.Contracts;

/// <summary>A group as seen in admin lists (spec &#167;3.1 <c>Group</c>). GET /groups.</summary>
/// <param name="GroupId">Surrogate int PK.</param>
/// <param name="GroupUnique">Externally-shared stable id.</param>
/// <param name="Name">Group display name.</param>
/// <param name="Description">Optional free-text description.</param>
/// <param name="EntraGroupId">
/// The linked Entra group's object id, when <paramref name="IsEntraSynced"/> is true
/// (spec &#167;3.1, &#167;7.3). Exposed so an admin can see and verify which Entra group a
/// FoundryGate group is linked to — previously settable via
/// <see cref="CreateGroupRequest"/> but unreadable afterward.
/// </param>
/// <param name="IsEntraSynced">True when <paramref name="EntraGroupId"/> is set and membership is kept in sync from Entra.</param>
/// <param name="IsUnlimited">When true, members of this group get unlimited quota unless overridden individually (spec &#167;3.2).</param>
/// <param name="MonthlyTokenQuota">Group-level quota override for members; null falls through the resolution chain (spec &#167;3.2).</param>
/// <param name="MemberCount">Number of users currently in the group.</param>
/// <param name="CreatedDate">When the group was created.</param>
public record GroupResponse(
    int GroupId,
    Guid GroupUnique,
    string Name,
    string? Description,
    string? EntraGroupId,
    bool IsEntraSynced,
    bool IsUnlimited,
    long? MonthlyTokenQuota,
    int MemberCount,
    DateTimeOffset CreatedDate);

/// <summary>Group detail with its full member roster. GET /groups/{id}.</summary>
public record GroupDetailResponse(
    GroupResponse Group,
    IReadOnlyList<GroupMemberResponse> Members);

/// <summary>One member of a group's roster.</summary>
/// <param name="UserId">The member's int PK.</param>
/// <param name="UserUnique">The member's externally-shared stable id.</param>
/// <param name="DisplayName">The member's display name.</param>
/// <param name="Email">The member's email.</param>
/// <param name="AddedDate">When this membership was created.</param>
/// <param name="AddedByUserId">
/// Null when the membership came from Entra group sync rather than an explicit admin action
/// (the data-layer entity is nullable for the same reason — reconciled in #92).
/// </param>
public record GroupMemberResponse(
    int UserId,
    Guid UserUnique,
    string DisplayName,
    string Email,
    DateTimeOffset AddedDate,
    int? AddedByUserId);

/// <summary>POST /groups body. Init-property record, not positional — see <see cref="Foundry.Contracts.CreateFoundryDeploymentRequest"/>'s remarks (#128).</summary>
public record CreateGroupRequest
{
    /// <summary>Group display name.</summary>
    [Required]
    [StringLength(ValidationConstants.GroupNameMaxLength, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional free-text description.</summary>
    [StringLength(ValidationConstants.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>
    /// Optional Entra group object id to link for &#167;7.3 group sync. Validated as
    /// GUID-shaped (Entra object ids are always GUIDs) rather than typed as <see cref="Guid"/>
    /// directly, to stay consistent with <c>User.EntraObjectId</c> — also a GUID-shaped
    /// <see cref="string"/> elsewhere in this domain (see <see cref="ValidationConstants.EntraObjectIdMaxLength"/>).
    /// </summary>
    [StringLength(ValidationConstants.EntraObjectIdMaxLength)]
    [RegularExpression(ValidationConstants.GuidPattern, ErrorMessage = "EntraGroupId must be a GUID (Entra object id).")]
    public string? EntraGroupId { get; init; }

    /// <summary>When true, members get unlimited quota unless overridden individually.</summary>
    public bool IsUnlimited { get; init; }

    /// <summary>Group-level quota override for members; null falls through the resolution chain (spec &#167;3.2).</summary>
    [Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    public long? MonthlyTokenQuota { get; init; }
}

/// <summary>PUT /groups/{id} body — admin updates name/description/quota (spec &#167;4.2).</summary>
public record UpdateGroupRequest
{
    /// <summary>Group display name.</summary>
    [Required]
    [StringLength(ValidationConstants.GroupNameMaxLength, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional free-text description.</summary>
    [StringLength(ValidationConstants.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>When true, members get unlimited quota unless overridden individually.</summary>
    public bool IsUnlimited { get; init; }

    /// <summary>Group-level quota override for members; null falls through the resolution chain (spec &#167;3.2).</summary>
    [Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    public long? MonthlyTokenQuota { get; init; }
}

/// <summary>POST /groups/{id}/members body.</summary>
public record AddGroupMemberRequest
{
    /// <summary>The user to add. An empty body binds to <c>0</c>, which <c>[Range]</c> rejects as a field-level 400.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "UserId must be a valid user id.")]
    public int UserId { get; init; }
}

/// <summary>Result of POST /groups/sync-entra for one group (spec &#167;7.3).</summary>
public record GroupSyncResult(int GroupId, int AddedCount, int RemovedCount);
