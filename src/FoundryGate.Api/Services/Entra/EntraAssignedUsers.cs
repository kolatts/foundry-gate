namespace FoundryGate.Api.Services.Entra;

/// <summary>
/// What <see cref="IEntraDirectoryClient.ListAssignedUsersAsync"/> found on the FoundryGate service
/// principal's app-role assignments: the user assignees, hydrated, and the group assignees, which are
/// <em>not</em> expanded to members yet (#121) and therefore have to be surfaced so the sync can
/// suspend departure detection instead of deactivating everyone such a group covers.
/// </summary>
/// <param name="Users">Distinct user assignees with their directory fields.</param>
/// <param name="SkippedGroupAssignments">Assignments whose principal is a group; empty when every assignee is a user.</param>
public sealed record EntraAssignedUsers(IReadOnlyList<EntraUser> Users, IReadOnlyList<EntraGroupAssignment> SkippedGroupAssignments);

/// <summary>A group that holds an app-role assignment on the FoundryGate application.</summary>
/// <param name="GroupObjectId">The group's Entra object id (<c>appRoleAssignment.principalId</c>).</param>
/// <param name="DisplayName">The group's display name (<c>appRoleAssignment.principalDisplayName</c>), for logs and the audit row.</param>
public sealed record EntraGroupAssignment(string GroupObjectId, string DisplayName);
