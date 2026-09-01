using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Domain.Groups.Contracts;

/// <summary>A group as seen in admin lists (spec &#167;3.1 <c>Group</c>). GET /groups.</summary>
public record GroupResponse(
    int GroupId,
    Guid GroupUnique,
    string Name,
    string? Description,
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
public record GroupMemberResponse(
    int UserId,
    Guid UserUnique,
    string DisplayName,
    string Email,
    DateTimeOffset AddedDate,
    int AddedByUserId);

/// <summary>POST /groups body.</summary>
public record CreateGroupRequest(
    [property: Required, StringLength(ValidationConstants.GroupNameMaxLength, MinimumLength = 1)]
    string Name,
    [property: StringLength(ValidationConstants.DescriptionMaxLength)]
    string? Description,
    string? EntraGroupId,
    bool IsUnlimited,
    [property: Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    long? MonthlyTokenQuota);

/// <summary>PUT /groups/{id} body — admin updates name/description/quota (spec &#167;4.2).</summary>
public record UpdateGroupRequest(
    [property: Required, StringLength(ValidationConstants.GroupNameMaxLength, MinimumLength = 1)]
    string Name,
    [property: StringLength(ValidationConstants.DescriptionMaxLength)]
    string? Description,
    bool IsUnlimited,
    [property: Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    long? MonthlyTokenQuota);

/// <summary>POST /groups/{id}/members body.</summary>
public record AddGroupMemberRequest([property: Range(1, int.MaxValue, ErrorMessage = "UserId must be a valid user id.")] int UserId);

/// <summary>Result of POST /groups/sync-entra for one group (spec &#167;7.3).</summary>
public record GroupSyncResult(int GroupId, int AddedCount, int RemovedCount);
